using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Networking;

namespace AirDataAnnouncer
{
    [BepInPlugin("com.yourname.airdataannouncer", "AirDataAnnouncer", "1.4.5")]
    public class AirDataAnnouncer : BaseUnityPlugin
    {
        public static AirDataAnnouncer Instance { get; private set; }

        public enum AltCalloutStyle
        {
            American, // "Twenty Five Hundred"
            British   // "Two Thousand Five Hundred"
        }

        // --- Sections ---
        const string SectionGeneral = "1. General";
        const string SectionAlt = "2. Altitude Callouts";
        const string SectionMach = "3. Mach Callouts";
        const string SectionGear = "4. Gear Callouts";
        const string SectionRunway = "5. Approach & Minimums";

        // --- Configuration ---
        internal static ConfigEntry<string> SoundPack;
        internal static ConfigEntry<float> MasterVolume;
        internal static ConfigEntry<bool> DebugMode;
        internal static ConfigEntry<bool> LogToFile;

        // Altitude Callouts
        internal static ConfigEntry<bool> Callout10;
        internal static ConfigEntry<bool> Callout20;
        internal static ConfigEntry<bool> Callout30;
        internal static ConfigEntry<bool> Callout40;
        internal static ConfigEntry<bool> Callout50;
        internal static ConfigEntry<bool> Callout75;
        internal static ConfigEntry<bool> Callout100;
        internal static ConfigEntry<bool> Callout200;
        internal static ConfigEntry<bool> Callout300;
        internal static ConfigEntry<bool> Callout400;
        internal static ConfigEntry<bool> Callout500;
        internal static ConfigEntry<bool> Callout1000;
        internal static ConfigEntry<bool> Callout2500;
        internal static ConfigEntry<AltCalloutStyle> Callout2500Style;

        // Overrides
        internal static Dictionary<string, ConfigEntry<string>> PackOverrides = new Dictionary<string, ConfigEntry<string>>();

        // Mach Callouts
        internal static Dictionary<int, ConfigEntry<bool>> MachCallouts = new Dictionary<int, ConfigEntry<bool>>();
        internal static ConfigEntry<bool> SubsonicCallouts;

        // Gear Callouts
        internal static ConfigEntry<bool> GearDownCallout;
        internal static ConfigEntry<bool> GearUpCallout;
        internal static ConfigEntry<bool> GearDownLockedCallout;
        internal static ConfigEntry<bool> GearUpLockedCallout;
        internal static ConfigEntry<bool> ContactCallout;

        // New Callouts
        internal static ConfigEntry<bool> CalloutRetard;
        internal static ConfigEntry<bool> CalloutMinimums;
        internal static ConfigEntry<int> MinimumsAltitude;
        internal static ConfigEntry<bool> Callout100Above;
        internal static ConfigEntry<bool> CalloutClearedToLand;
        internal static ConfigEntry<bool> CalloutPullUp;
        internal static ConfigEntry<float> PullUpSensitivity; // New Config

        // --- State ---
        public static Aircraft CurrentAircraft;
        public LandingGear[] CurrentLandingGears;
        private AudioSource audioSource;

        // Cache: Key = CalloutName (e.g. "10"), Value = AudioClip
        public Dictionary<string, AudioClip> clipCache = new Dictionary<string, AudioClip>();

        // Audio Queue System
        private struct QueuedClip
        {
            public string Name;
            public AudioClip Clip;
            public bool IsAltitude;
        }
        private Queue<QueuedClip> audioQueue = new Queue<QueuedClip>();
        private string currentPlayingClipName = "";

        // Available Packs for UI
        public string[] availablePacks;
        public string[] availablePacksWithDefault;

        // UI State for Dropdowns
        public Dictionary<string, bool> dropdownStates = new Dictionary<string, bool>();

        // Tracking Variables
        private float prevRadarAlt;
        private Vector3 prevPos; // Track 3D Position for manual velocity
        private Vector3 currentVelocity; // Calculated manually
        private float smoothedVerticalSpeed; // For smoothing
        private float prevMach;

        // Gear Tracking
        private bool prevGearDeployed;
        private LandingGear.GearState prevGearState;

        // Contact Tracking
        private bool wasGrounded;
        private bool contactArmed = false;

        // Approach Tracking
        private bool minimumsCalloutPlayed = false;
        private bool hundredAboveCalloutPlayed = false;
        private float lastClearedToLandTime = -999f;
        private float lastRetardTime = -999f;
        private float lastPullUpTime = -999f;

        private bool soundsLoaded = false;
        private string debugMessage = "";
        private float debugMessageTimer = 0f;

        // Reflection Cache
        private bool reflectionInitialized = false;
        private object playerSettingsInstance;
        private MemberInfo unitSystemMember;
        private FieldInfo throttleField;

        private void Awake()
        {
            Instance = this;

            RefreshSoundPackList();
            SetupConfig();
            EnsureAudioSourceExists();

            var harmony = Harmony.CreateAndPatchAll(typeof(AirDataAnnouncer));

            try
            {
                var reportType = AccessTools.TypeByName("AircraftActionsReport");
                if (reportType != null)
                {
                    var original = AccessTools.Method(reportType, "ReportText");
                    var patch = AccessTools.Method(typeof(AirDataAnnouncer), nameof(ReportTextHook));

                    if (original != null && patch != null)
                    {
                        harmony.Patch(original, postfix: new HarmonyMethod(patch));
                        Logger.LogInfo("Successfully hooked AircraftActionsReport.ReportText");
                    }
                }
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"Failed to hook text report: {ex.Message}");
            }

            StartCoroutine(LoadAllSounds());

            Logger.LogInfo("AirDataAnnouncer initialized.");
        }

        public static void ReportTextHook(string report)
        {
            if (Instance == null || string.IsNullOrEmpty(report)) return;

            if (LogToFile.Value) Instance.Logger.LogInfo($"[ReportText]: {report}");

            if (!CalloutClearedToLand.Value) return;

            if (Time.time - Instance.lastClearedToLandTime < 5f) return;

            if (report.IndexOf("Cleared for landing", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Instance.PlaySound("Cleared To Land");
                Instance.lastClearedToLandTime = Time.time;
            }
        }

        private void RefreshSoundPackList()
        {
            string basePath = Path.Combine(Paths.PluginPath, "AirDataAnnouncer", "Soundpacks");
            if (Directory.Exists(basePath))
            {
                availablePacks = Directory.GetDirectories(basePath).Select(Path.GetFileName).ToArray();
            }
            else
            {
                availablePacks = new string[] { "Altea" };
            }

            var list = new List<string> { "Default" };
            list.AddRange(availablePacks);
            availablePacksWithDefault = list.ToArray();
        }

        private void Update()
        {
            if (debugMessageTimer > 0)
            {
                debugMessageTimer -= Time.deltaTime;
                if (debugMessageTimer <= 0) debugMessage = "";
            }

            // Allow audio processing in main menu (previews)
            if (!soundsLoaded) return;
            ProcessAudioQueue();

            // Stop here if no aircraft (Main Menu)
            if (CurrentAircraft == null) return;

            // Calculate 3D Velocity & Vertical Speed manually
            // This bypasses the need for Rigidbody or Physics assemblies
            Vector3 currentPos = CurrentAircraft.transform.position;
            if (Time.deltaTime > 0)
            {
                Vector3 delta = currentPos - prevPos;
                // Floating Origin Check: If we moved impossibly fast (> 2000m/s in one frame), 
                // it's likely an origin shift. Skip velocity update.
                if (delta.sqrMagnitude < 4000000f)
                {
                    currentVelocity = delta / Time.deltaTime;
                }
            }
            prevPos = currentPos;

            // Smooth Vertical Speed (Simple Low Pass Filter)
            smoothedVerticalSpeed = Mathf.Lerp(smoothedVerticalSpeed, currentVelocity.y, Time.deltaTime * 5f);

            ProcessAltitude();
            ProcessGPWS(smoothedVerticalSpeed); // Pass Smoothed Vertical Speed
            ProcessMach();
            ProcessGear();
            ProcessContact();
        }

        private void EnsureAudioSourceExists()
        {
            if (audioSource == null)
            {
                var audioObj = new GameObject("AirDataAnnouncer_Audio");
                DontDestroyOnLoad(audioObj);
                audioSource = audioObj.AddComponent<AudioSource>();
            }
        }

        private void OnGUI()
        {
            if (!DebugMode.Value || string.IsNullOrEmpty(debugMessage)) return;

            var prevColor = GUI.color;
            var prevSize = GUI.skin.label.fontSize;
            var prevAlign = GUI.skin.label.alignment;

            GUI.color = Color.yellow;
            GUI.skin.label.fontSize = 32;
            GUI.skin.label.alignment = TextAnchor.UpperCenter;

            GUI.color = Color.black;
            GUI.Label(new Rect((Screen.width / 2) - 200 + 2, 80 + 2, 400, 100), debugMessage);

            GUI.color = Color.yellow;
            GUI.Label(new Rect((Screen.width / 2) - 200, 80, 400, 100), debugMessage);

            GUI.color = prevColor;
            GUI.skin.label.fontSize = prevSize;
            GUI.skin.label.alignment = prevAlign;
        }

        private void ProcessAudioQueue()
        {
            EnsureAudioSourceExists();

            if (audioSource.isPlaying)
            {
                if (currentPlayingClipName == "Retard" && CurrentAircraft != null)
                {
                    if (GetThrottle() <= 0.05f)
                    {
                        audioSource.Stop();
                        if (DebugMode.Value) debugMessage = "";
                    }
                }
                return;
            }

            if (audioQueue.Count > 0)
            {
                var next = audioQueue.Dequeue();
                currentPlayingClipName = next.Name;
                audioSource.clip = next.Clip;
                audioSource.volume = MasterVolume.Value;
                audioSource.Play();
            }
            else
            {
                currentPlayingClipName = "";
            }
        }

        public void PlaySound(string clipName, bool isAltitude = false)
        {
            if (DebugMode.Value)
            {
                debugMessage = $"GPWS: {clipName.ToUpper()}";
                debugMessageTimer = 2.0f;
            }

            EnsureAudioSourceExists();

            if (clipCache.TryGetValue(clipName, out AudioClip clip))
            {
                if (clip != null)
                {
                    if (isAltitude)
                    {
                        var filteredList = audioQueue.Where(x => !x.IsAltitude).ToList();
                        if (LogToFile.Value && filteredList.Count < audioQueue.Count)
                        {
                            Logger.LogInfo($"Skipping {audioQueue.Count - filteredList.Count} stale altitude callouts for {clipName}");
                        }
                        audioQueue = new Queue<QueuedClip>(filteredList);
                    }

                    audioQueue.Enqueue(new QueuedClip { Name = clipName, Clip = clip, IsAltitude = isAltitude });
                }
                else if (LogToFile.Value)
                {
                    Logger.LogWarning($"Sound '{clipName}' key exists but clip is null.");
                }
            }
            else
            {
                if (DebugMode.Value && LogToFile.Value)
                {
                    Logger.LogWarning($"Sound '{clipName}' not found in cache.");
                    debugMessage += " (MISSING)";
                }
            }
        }

        private void ProcessGPWS(float verticalSpeed)
        {
            if (!CalloutPullUp.Value) return;
            if (Time.time - lastPullUpTime < 1.5f) return;

            // Inhibit if nose is up (> 20 degrees, approx 0.34 in forward.y)
            float forwardY = CurrentAircraft.transform.forward.y;
            if (forwardY > 0.34f) return;

            bool trigger = false;
            float radarAlt = CurrentAircraft.radarAlt;

            // Mode 1: Excessive Sink Rate (Basic) - Predicts impact if vertical speed is maintained
            float sinkRate = -verticalSpeed;
            float warningTimeBase = 3.0f * PullUpSensitivity.Value;

            if (sinkRate > 1.0f)
            {
                float tti = radarAlt / sinkRate;
                if (tti < warningTimeBase) trigger = true;
            }

            // Mode 2: Forward Look (EGPWS) - Raycast
            // Predicts impact along current trajectory (velocity vector)
            if (!trigger)
            {
                float speed = currentVelocity.magnitude;
                if (speed > 30f) // Only active above ~60kts
                {
                    // Calculate look ahead distance based on speed and time threshold
                    float lookAheadDist = speed * warningTimeBase;
                    Vector3 direction = currentVelocity.normalized;

                    // FIX: Start ray 20m ahead to avoid hitting own aircraft collider
                    Vector3 startPos = CurrentAircraft.transform.position + (direction * 20f);

                    // Cast ray along velocity vector
                    if (Physics.Raycast(startPos, direction, out RaycastHit hit, lookAheadDist))
                    {
                        // We hit something within our impact time window
                        trigger = true;

                        if (DebugMode.Value && LogToFile.Value)
                            Logger.LogInfo($"EGPWS Raycast Hit: {hit.collider.name} at {hit.distance:F1}m");
                    }
                }
            }

            if (trigger)
            {
                PlaySound("Pull Up");
                lastPullUpTime = Time.time;
            }
        }

        private void ProcessContact()
        {
            if (!ContactCallout.Value || CurrentLandingGears == null) return;

            bool isGrounded = false;
            foreach (var gear in CurrentLandingGears) { if (gear.WeightOnWheel(0.1f)) { isGrounded = true; break; } }

            if (!contactArmed && !isGrounded)
            {
                if (CurrentAircraft.radarAlt > 2.0f) contactArmed = true;
            }

            if (contactArmed && isGrounded && !wasGrounded)
            {
                PlaySound("contact");
                contactArmed = false;
            }
            wasGrounded = isGrounded;
        }

        private void ProcessGear()
        {
            bool currentDeployed = CurrentAircraft.gearDeployed;
            if (currentDeployed != prevGearDeployed)
            {
                if (currentDeployed && GearDownCallout.Value) PlaySound("Gear Down");
                else if (!currentDeployed && GearUpCallout.Value) PlaySound("Gear Up");
            }
            prevGearDeployed = currentDeployed;

            LandingGear.GearState currentState = CurrentAircraft.gearState;
            if (currentState != prevGearState)
            {
                if (currentState == LandingGear.GearState.LockedExtended && GearDownLockedCallout.Value)
                {
                    PlaySound("Gear Down and Locked");
                }
                else
                {
                    string stateName = currentState.ToString();
                    bool isTransient = stateName.Contains("ing") || stateName.Contains("Ing");
                    bool isRetractedState = (stateName.Contains("Retract") || stateName.Contains("Stow") || (stateName.Contains("Up") && stateName.Contains("Lock")));

                    if (isRetractedState && !isTransient && GearUpLockedCallout.Value)
                    {
                        PlaySound("Gear Up and Locked");
                    }
                }
            }
            prevGearState = currentState;
        }

        private void ProcessAltitude()
        {
            if (!CurrentAircraft.gearDeployed)
            {
                minimumsCalloutPlayed = false;
                hundredAboveCalloutPlayed = false;
                return;
            }

            float currentAlt = CurrentAircraft.radarAlt;
            if (IsImperial()) currentAlt *= 3.28084f;

            if (CalloutRetard.Value)
            {
                bool shouldRetard = (prevRadarAlt > 20 && currentAlt <= 20) || (prevRadarAlt > 10 && currentAlt <= 10);

                if (shouldRetard && (Time.time - lastRetardTime > 1.0f))
                {
                    float throttle = GetThrottle();
                    if (throttle > 0.05f)
                    {
                        PlaySound("Retard");
                        lastRetardTime = Time.time;
                    }
                }
            }

            if (CalloutMinimums.Value)
            {
                float min = MinimumsAltitude.Value;
                if (prevRadarAlt > min && currentAlt <= min)
                {
                    PlaySound("Minimum");
                    minimumsCalloutPlayed = true;
                }
                else if (currentAlt > min + 200) minimumsCalloutPlayed = false;
            }

            if (Callout100Above.Value)
            {
                float thresh = MinimumsAltitude.Value + 100f;
                if (prevRadarAlt > thresh && currentAlt <= thresh)
                {
                    PlaySound("100Above");
                    hundredAboveCalloutPlayed = true;
                }
                else if (currentAlt > thresh + 200) hundredAboveCalloutPlayed = false;
            }

            CheckAltThreshold(2500, currentAlt, Callout2500, Callout2500Style.Value);
            CheckAltThreshold(1000, currentAlt, Callout1000);
            CheckAltThreshold(500, currentAlt, Callout500);
            CheckAltThreshold(400, currentAlt, Callout400);
            CheckAltThreshold(300, currentAlt, Callout300);
            CheckAltThreshold(200, currentAlt, Callout200);
            CheckAltThreshold(100, currentAlt, Callout100);
            CheckAltThreshold(75, currentAlt, Callout75);
            CheckAltThreshold(50, currentAlt, Callout50);
            CheckAltThreshold(40, currentAlt, Callout40);
            CheckAltThreshold(30, currentAlt, Callout30);
            CheckAltThreshold(20, currentAlt, Callout20);
            CheckAltThreshold(10, currentAlt, Callout10);

            prevRadarAlt = currentAlt;
        }

        private float GetThrottle()
        {
            try
            {
                if (throttleField == null)
                {
                    // Prefer "throttle" field based on user token
                    throttleField = CurrentAircraft.GetType().GetField("throttle", BindingFlags.Public | BindingFlags.Instance)
                                 ?? CurrentAircraft.GetType().GetField("inputThrottle", BindingFlags.Public | BindingFlags.Instance);
                }
                if (throttleField != null) return (float)throttleField.GetValue(CurrentAircraft);
            }
            catch { }
            return 1.0f;
        }

        private bool IsImperial()
        {
            if (!reflectionInitialized)
            {
                InitializeReflection();
                reflectionInitialized = true;
            }
            if (unitSystemMember == null) return false;

            try
            {
                object val = null;
                if (unitSystemMember is PropertyInfo p) val = p.GetValue(playerSettingsInstance, null);
                else if (unitSystemMember is FieldInfo f) val = f.GetValue(playerSettingsInstance);

                if (val != null)
                {
                    string s = val.ToString();
                    return s.Equals("Imperial", System.StringComparison.OrdinalIgnoreCase) || s == "1";
                }
            }
            catch { }
            return false;
        }

        private void InitializeReflection()
        {
            try
            {
                Type psType = AccessTools.TypeByName("PlayerSettings");
                if (psType == null) return;

                var members = psType.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                foreach (var m in members)
                {
                    if (m.Name.Equals("unitSystem", System.StringComparison.OrdinalIgnoreCase) ||
                        m.Name.Equals("UnitSystem", System.StringComparison.OrdinalIgnoreCase))
                    {
                        if (m is PropertyInfo p && p.PropertyType.IsEnum) { unitSystemMember = p; break; }
                        if (m is FieldInfo f && f.FieldType.IsEnum) { unitSystemMember = f; break; }
                    }
                }

                bool isStatic = false;
                if (unitSystemMember is PropertyInfo prop) isStatic = prop.GetGetMethod(true).IsStatic;
                else if (unitSystemMember is FieldInfo field) isStatic = field.IsStatic;

                if (unitSystemMember != null && !isStatic)
                {
                    var instProp = AccessTools.Property(psType, "Instance") ?? AccessTools.Property(psType, "instance");
                    if (instProp != null) playerSettingsInstance = instProp.GetValue(null, null);
                }
            }
            catch (System.Exception ex) { Logger.LogError($"Reflection Init Failed: {ex.Message}"); }
        }

        private void ProcessMach()
        {
            float speedOfSound = 343f; // Fixed constant to avoid LevelInfo missing member error
            float currentMach = CurrentAircraft.speed / speedOfSound;

            if (SubsonicCallouts.Value)
            {
                if (prevMach >= 1.0f && currentMach < 1.0f) PlaySound("subsonic");
            }

            if (Mathf.Floor(currentMach) > Mathf.Floor(prevMach))
            {
                int machNumber = (int)Mathf.Floor(currentMach);
                if (MachCallouts.ContainsKey(machNumber) && MachCallouts[machNumber].Value)
                {
                    PlaySound($"Mach {machNumber}");
                }
            }
            prevMach = currentMach;
        }

        private void CheckAltThreshold(float threshold, float current, ConfigEntry<bool> config, AltCalloutStyle style = AltCalloutStyle.American)
        {
            if (!config.Value) return;

            if (prevRadarAlt > threshold && current <= threshold)
            {
                string clipName = threshold.ToString();
                if (threshold == 2500 && style == AltCalloutStyle.British) clipName = "2500_UK";
                PlaySound(clipName, true);
            }
        }

        private IEnumerator LoadAllSounds()
        {
            soundsLoaded = false;
            clipCache.Clear();

            string defaultPackName = SoundPack.Value;
            string basePath = Path.Combine(Paths.PluginPath, "AirDataAnnouncer", "Soundpacks");

            if (LogToFile.Value) Logger.LogInfo($"Loading sounds. Master Pack: {defaultPackName}");

            var keys = new List<string> {
                "10", "20", "30", "40", "50", "75", "100", "200", "300", "400", "500", "1000", "2500", "2500_UK",
                "subsonic", "Gear Down", "Gear Up", "Gear Down and Locked", "Gear Up and Locked", "contact",
                "Retard", "Minimum", "100Above", "Cleared To Land", "Pull Up"
            };

            for (int i = 1; i <= 10; i++) keys.Add($"Mach {i}");

            foreach (var key in keys)
            {
                string packToUse = defaultPackName;
                if (PackOverrides.ContainsKey(key))
                {
                    string overrideVal = PackOverrides[key].Value;
                    if (!string.IsNullOrEmpty(overrideVal) && overrideVal != "Default")
                    {
                        packToUse = overrideVal;
                    }
                }

                string packPath = Path.Combine(basePath, packToUse);
                string filename = key;

                string filePath = Path.Combine(packPath, filename + ".wav");
                AudioType type = AudioType.WAV;

                if (!File.Exists(filePath))
                {
                    filePath = Path.Combine(packPath, filename + ".ogg");
                    type = AudioType.OGGVORBIS;
                }

                if (File.Exists(filePath))
                {
                    using (UnityWebRequest uwr = UnityWebRequestMultimedia.GetAudioClip("file://" + filePath, type))
                    {
                        yield return uwr.SendWebRequest();
                        if (uwr.result == UnityWebRequest.Result.Success)
                        {
                            var clip = DownloadHandlerAudioClip.GetContent(uwr);
                            if (clip != null)
                            {
                                clip.name = filename;
                                clipCache[key] = clip;
                            }
                        }
                    }
                }
            }

            soundsLoaded = true;
            if (LogToFile.Value) Logger.LogInfo($"Loaded {clipCache.Count} sounds.");
        }

        private void SetupConfig()
        {
            DebugMode = Config.Bind(SectionGeneral, "Debug Mode", false, "Show visual text on screen");
            LogToFile = Config.Bind(SectionGeneral, "Log To File", false, "Enable logging to the BepInEx console/file");
            MasterVolume = Config.Bind(SectionGeneral, "Master Volume", 1.0f, new ConfigDescription("Volume", new AcceptableValueRange<float>(0f, 1f)));

            SoundPack = Config.Bind(SectionGeneral, "Master Sound Pack", availablePacks.FirstOrDefault() ?? "Altea",
                new ConfigDescription("Global sound pack", new AcceptableValueList<string>(availablePacks)));

            SoundPack.SettingChanged += (s, a) => { if (this) StartCoroutine(LoadAllSounds()); };

            BindCallout(SectionAlt, "10 ft Callout", "10", ref Callout10);
            BindCallout(SectionAlt, "20 ft Callout", "20", ref Callout20);
            BindCallout(SectionAlt, "30 ft Callout", "30", ref Callout30);
            BindCallout(SectionAlt, "40 ft Callout", "40", ref Callout40);
            BindCallout(SectionAlt, "50 ft Callout", "50", ref Callout50);
            BindCallout(SectionAlt, "75 ft Callout", "75", ref Callout75);
            BindCallout(SectionAlt, "100 ft Callout", "100", ref Callout100);
            BindCallout(SectionAlt, "200 ft Callout", "200", ref Callout200);
            BindCallout(SectionAlt, "300 ft Callout", "300", ref Callout300);
            BindCallout(SectionAlt, "400 ft Callout", "400", ref Callout400);
            BindCallout(SectionAlt, "500 ft Callout", "500", ref Callout500);
            BindCallout(SectionAlt, "1000 ft Callout", "1000", ref Callout1000);
            BindCallout2500(SectionAlt, "2500 ft Callout", ref Callout2500);

            BindCallout(SectionMach, "Subsonic", "subsonic", ref SubsonicCallouts);
            MachCallouts.Clear();
            for (int i = 1; i <= 10; i++)
            {
                ConfigEntry<bool> entry = null;
                BindCallout(SectionMach, $"Mach {i}", $"Mach {i}", ref entry);
                MachCallouts.Add(i, entry);
            }

            BindCallout(SectionGear, "Gear Down", "Gear Down", ref GearDownCallout);
            BindCallout(SectionGear, "Gear Up", "Gear Up", ref GearUpCallout);
            BindCallout(SectionGear, "Gear Down & Locked", "Gear Down and Locked", ref GearDownLockedCallout);
            BindCallout(SectionGear, "Gear Up & Locked", "Gear Up and Locked", ref GearUpLockedCallout);
            BindCallout(SectionGear, "Contact", "contact", ref ContactCallout);

            BindCallout(SectionRunway, "Retard", "Retard", ref CalloutRetard, "Airbus");

            MinimumsAltitude = Config.Bind(SectionRunway, "Minimums Altitude", 200, "Altitude for Minimums callout");
            BindCallout(SectionRunway, "Minimums Callout", "Minimum", ref CalloutMinimums, "Airbus");
            BindCallout(SectionRunway, "100 Above Callout", "100Above", ref Callout100Above, "Airbus");

            BindCallout(SectionRunway, "Cleared To Land", "Cleared To Land", ref CalloutClearedToLand);

            // New Config for Pull Up
            BindCallout(SectionRunway, "Pull Up Warning", "Pull Up", ref CalloutPullUp, "Betty");
            PullUpSensitivity = Config.Bind(SectionRunway, "Pull Up Sensitivity", 0.486f, new ConfigDescription("Adjust sensitivity of GPWS Pull Up warning (Higher = More Sensitive)", new AcceptableValueRange<float>(0.1f, 2.0f)));
        }

        private void BindCallout(string section, string key, string audioClipName, ref ConfigEntry<bool> entry, string defaultPack = "Default")
        {
            var overrideEntry = Config.Bind(section, key + "_Pack", defaultPack,
                new ConfigDescription("Override", new AcceptableValueList<string>(availablePacksWithDefault),
                new ConfigurationManagerAttributes { Browsable = false }));

            PackOverrides[audioClipName] = overrideEntry;
            overrideEntry.SettingChanged += (s, a) => { if (this) StartCoroutine(LoadAllSounds()); };

            var description = new ConfigDescription(
                $"Enable {key}",
                null,
                new ConfigurationManagerAttributes
                {
                    CustomDrawer = (ConfigEntryBase e) =>
                    {
                        GUILayout.BeginHorizontal();
                        bool exists = Instance != null && Instance.clipCache.ContainsKey(audioClipName);
                        GUI.enabled = exists;
                        if (GUILayout.Button("▶", GUILayout.Width(25))) Instance?.PlaySound(audioClipName);
                        GUI.enabled = true;
                        GUILayout.Space(5);

                        bool value = (bool)e.BoxedValue;
                        var originalColor = GUI.color;
                        if (!exists) GUI.color = Color.red;
                        bool newValue = GUILayout.Toggle(value, new GUIContent(key, exists ? null : "Sound file missing in selected pack"));
                        if (!exists) GUILayout.Label("(Missing)", GUILayout.ExpandWidth(false));
                        GUI.color = originalColor;
                        if (newValue != value) e.BoxedValue = newValue;

                        GUILayout.FlexibleSpace();
                        GUILayout.Label("Pack:");
                        string currentOverride = overrideEntry.Value;

                        if (!Instance.dropdownStates.ContainsKey(key)) Instance.dropdownStates[key] = false;

                        if (GUILayout.Button(currentOverride, GUILayout.Width(150)))
                        {
                            Instance.dropdownStates[key] = !Instance.dropdownStates[key];
                        }
                        GUILayout.EndHorizontal();

                        if (Instance.dropdownStates[key])
                        {
                            GUILayout.BeginVertical(GUI.skin.box);
                            foreach (var pack in Instance.availablePacksWithDefault)
                            {
                                if (GUILayout.Button(pack))
                                {
                                    overrideEntry.Value = pack;
                                    Instance.dropdownStates[key] = false;
                                }
                            }
                            GUILayout.EndVertical();
                        }
                    }
                }
            );
            entry = Config.Bind(section, key, true, description);
        }

        private void BindCallout2500(string section, string key, ref ConfigEntry<bool> entry)
        {
            Callout2500Style = Config.Bind(section, "2500 ft Style", AltCalloutStyle.American,
                new ConfigDescription("Style", null, new ConfigurationManagerAttributes { Browsable = false }));

            var overrideEntry = Config.Bind(section, key + "_Pack", "Default",
                new ConfigDescription("Override", new AcceptableValueList<string>(availablePacksWithDefault),
                new ConfigurationManagerAttributes { Browsable = false }));
            PackOverrides["2500"] = overrideEntry;
            PackOverrides["2500_UK"] = overrideEntry;

            overrideEntry.SettingChanged += (s, a) => { if (this) StartCoroutine(LoadAllSounds()); };

            var description = new ConfigDescription(
                $"Enable {key}",
                null,
                new ConfigurationManagerAttributes
                {
                    CustomDrawer = (ConfigEntryBase e) =>
                    {
                        string clipToPlay = (Callout2500Style.Value == AltCalloutStyle.British) ? "2500_UK" : "2500";
                        bool exists = Instance != null && Instance.clipCache.ContainsKey(clipToPlay);

                        GUILayout.BeginHorizontal();
                        GUI.enabled = exists;
                        if (GUILayout.Button("▶", GUILayout.Width(25))) Instance?.PlaySound(clipToPlay);
                        GUI.enabled = true;
                        GUILayout.Space(5);

                        bool value = (bool)e.BoxedValue;
                        var originalColor = GUI.color;
                        if (!exists) GUI.color = Color.red;
                        bool newValue = GUILayout.Toggle(value, new GUIContent(key, exists ? null : "Sound file missing"));
                        if (!exists) GUILayout.Label("(Missing)", GUILayout.ExpandWidth(false));
                        GUI.color = originalColor;
                        if (newValue != value) e.BoxedValue = newValue;

                        GUILayout.FlexibleSpace();
                        GUILayout.Label("Style:");
                        int selStyle = (int)Callout2500Style.Value;
                        string[] styleOpts = { "US", "UK" };
                        int newStyle = GUILayout.Toolbar(selStyle, styleOpts, GUILayout.Width(80));
                        if (newStyle != selStyle) Callout2500Style.Value = (AltCalloutStyle)newStyle;

                        GUILayout.Space(5);

                        if (!Instance.dropdownStates.ContainsKey(key)) Instance.dropdownStates[key] = false;

                        if (GUILayout.Button(overrideEntry.Value, GUILayout.Width(100)))
                        {
                            Instance.dropdownStates[key] = !Instance.dropdownStates[key];
                        }
                        GUILayout.EndHorizontal();

                        if (Instance.dropdownStates[key])
                        {
                            GUILayout.BeginVertical(GUI.skin.box);
                            foreach (var pack in Instance.availablePacksWithDefault)
                            {
                                if (GUILayout.Button(pack))
                                {
                                    overrideEntry.Value = pack;
                                    Instance.dropdownStates[key] = false;
                                }
                            }
                            GUILayout.EndVertical();
                        }
                    }
                }
            );
            entry = Config.Bind(section, key, true, description);
        }

        [HarmonyPatch(typeof(Altitude), "Initialize")]
        [HarmonyPostfix]
        public static void HookCurrentAircraft(Aircraft aircraft)
        {
            CurrentAircraft = aircraft;
            if (Instance != null)
            {
                Instance.CurrentLandingGears = aircraft.GetComponentsInChildren<LandingGear>();
                Instance.prevGearDeployed = aircraft.gearDeployed;
                Instance.prevGearState = aircraft.gearState;
                Instance.prevRadarAlt = aircraft.radarAlt;
                Instance.prevPos = aircraft.transform.position; // Init position
                Instance.wasGrounded = true;
                Instance.contactArmed = false;

                Instance.minimumsCalloutPlayed = false;
                Instance.hundredAboveCalloutPlayed = false;
                Instance.throttleField = null;
            }
        }
    }

    public class ConfigurationManagerAttributes
    {
        public System.Action<ConfigEntryBase> CustomDrawer;
        public bool? ShowRangeAsPercent;
        public bool? ReadOnly;
        public bool? HideDefaultButton;
        public bool? HideSettingName;
        public bool? Browsable;
        public string Description;
        public string DispName;
        public int? Order;
    }
}