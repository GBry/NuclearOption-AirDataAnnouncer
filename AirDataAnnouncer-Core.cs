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
    [BepInPlugin("com.yourname.airdataannouncer", "AirDataAnnouncer", "1.1.0")]
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

        // --- Configuration ---
        internal static ConfigEntry<string> SoundPack;
        internal static ConfigEntry<float> MasterVolume;
        internal static ConfigEntry<bool> DebugMode;

        // Altitude Callouts (0 removed)
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

        // Mach Callouts
        internal static Dictionary<int, ConfigEntry<bool>> MachCallouts = new Dictionary<int, ConfigEntry<bool>>();
        internal static ConfigEntry<bool> SubsonicCallouts;

        // Gear Callouts
        internal static ConfigEntry<bool> GearDownCallout;
        internal static ConfigEntry<bool> GearUpCallout;
        internal static ConfigEntry<bool> GearDownLockedCallout;
        internal static ConfigEntry<bool> GearUpLockedCallout;
        internal static ConfigEntry<bool> ContactCallout;

        // --- State ---
        public static Aircraft CurrentAircraft;
        public LandingGear[] CurrentLandingGears;
        private AudioSource audioSource;
        public Dictionary<string, AudioClip> clipCache = new Dictionary<string, AudioClip>();

        // Tracking Variables
        private float prevRadarAlt;
        private float prevMach;

        // Gear Tracking
        private bool prevGearDeployed;
        private LandingGear.GearState prevGearState;

        // Contact Tracking
        private bool wasGrounded;
        private bool contactArmed = false; // Controls if "contact" is allowed to play

        private bool soundsLoaded = false;
        private string debugMessage = "";
        private float debugMessageTimer = 0f;

        // Reflection Cache
        private bool reflectionInitialized = false;
        private object playerSettingsInstance;
        private MemberInfo unitSystemMember;

        private void Awake()
        {
            Instance = this;

            SetupConfig();
            EnsureAudioSourceExists();
            Harmony.CreateAndPatchAll(typeof(AirDataAnnouncer));
            StartCoroutine(LoadSoundPack());

            Logger.LogInfo("AirDataAnnouncer initialized.");
        }

        private void Update()
        {
            if (debugMessageTimer > 0)
            {
                debugMessageTimer -= Time.deltaTime;
                if (debugMessageTimer <= 0) debugMessage = "";
            }

            if (CurrentAircraft == null || !soundsLoaded) return;

            ProcessAltitude();
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

        // --- Visual Debugging ---
        private void OnGUI()
        {
            if (!DebugMode.Value || string.IsNullOrEmpty(debugMessage)) return;

            var prevColor = GUI.color;
            var prevSize = GUI.skin.label.fontSize;
            var prevAlign = GUI.skin.label.alignment;

            GUI.color = Color.yellow;
            GUI.skin.label.fontSize = 32;
            GUI.skin.label.alignment = TextAnchor.UpperCenter;

            // Shadow
            GUI.color = Color.black;
            GUI.Label(new Rect((Screen.width / 2) - 200 + 2, 80 + 2, 400, 100), debugMessage);

            // Text
            GUI.color = Color.yellow;
            GUI.Label(new Rect((Screen.width / 2) - 200, 80, 400, 100), debugMessage);

            GUI.color = prevColor;
            GUI.skin.label.fontSize = prevSize;
            GUI.skin.label.alignment = prevAlign;
        }

        // --- Logic Loops ---

        private void ProcessContact()
        {
            if (!ContactCallout.Value || CurrentLandingGears == null) return;

            // 1. Determine Grounded State
            bool isGrounded = false;
            foreach (var gear in CurrentLandingGears)
            {
                if (gear.WeightOnWheel(0.1f))
                {
                    isGrounded = true;
                    break;
                }
            }

            // 2. Arming Logic
            // Arm if we are NOT grounded and have climbed slightly (to avoid jitter while taxiing)
            // Or if we are high enough.
            if (!contactArmed && !isGrounded)
            {
                if (CurrentAircraft.radarAlt > 2.0f)
                {
                    contactArmed = true;
                    if (DebugMode.Value) Logger.LogInfo("Contact Callout ARMED");
                }
            }

            // 3. Trigger Logic
            // Fire if armed, we just touched down (isGrounded && !wasGrounded)
            if (contactArmed && isGrounded && !wasGrounded)
            {
                PlaySound("contact");
                contactArmed = false; // Disarm until next takeoff
                if (DebugMode.Value) Logger.LogInfo("Contact Callout FIRED & DISARMED");
            }

            wasGrounded = isGrounded;
        }

        private void ProcessGear()
        {
            // 1. Gear Switch (Command) Logic
            bool currentDeployed = CurrentAircraft.gearDeployed;
            if (currentDeployed != prevGearDeployed)
            {
                if (DebugMode.Value) Logger.LogInfo($"Gear Switch Changed: {currentDeployed}");

                if (currentDeployed && GearDownCallout.Value) PlaySound("Gear Down");
                else if (!currentDeployed && GearUpCallout.Value) PlaySound("Gear Up");
            }
            prevGearDeployed = currentDeployed;

            // 2. Gear Physical State (Locked) Logic
            LandingGear.GearState currentState = CurrentAircraft.gearState;
            if (currentState != prevGearState)
            {
                if (DebugMode.Value) Logger.LogInfo($"Gear State Transition: {prevGearState} -> {currentState}");

                // Down & Locked
                if (currentState == LandingGear.GearState.LockedExtended && GearDownLockedCallout.Value)
                {
                    PlaySound("Gear Down and Locked");
                }
                else
                {
                    // Transition TO Up & Locked
                    string stateName = currentState.ToString();

                    // Filter transient states (Retracting, Extending)
                    bool isTransient = stateName.Contains("ing") || stateName.Contains("Ing");

                    // Check for variations of Up/Retracted
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
            if (!CurrentAircraft.gearDeployed) return;

            float currentAlt = CurrentAircraft.radarAlt;

            // Bind units to game setting: Metric (Default) or Imperial
            // Uses reflection to find the setting to avoid "Type is not a variable" errors
            if (IsImperial())
            {
                currentAlt *= 3.28084f; // Convert Meters to Feet
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
            // 0 removed

            prevRadarAlt = currentAlt;
        }

        private bool IsImperial()
        {
            if (!reflectionInitialized)
            {
                InitializeReflection();
                reflectionInitialized = true;
            }

            // If we failed to find the setting, assume Metric (false)
            if (unitSystemMember == null) return false;

            try
            {
                object val = null;
                if (unitSystemMember is PropertyInfo p)
                    val = p.GetValue(playerSettingsInstance, null);
                else if (unitSystemMember is FieldInfo f)
                    val = f.GetValue(playerSettingsInstance);

                // Check for "Imperial" (case-insensitive) or value 1
                if (val != null)
                {
                    string s = val.ToString();
                    return s.Equals("Imperial", System.StringComparison.OrdinalIgnoreCase) || s == "1";
                }
            }
            catch
            {
                // Silently fail to default
            }

            return false;
        }

        private void InitializeReflection()
        {
            try
            {
                // 1. Find PlayerSettings type
                Type psType = AccessTools.TypeByName("PlayerSettings");
                if (psType == null) return;

                // 2. Look for the UnitSystem member (field or property)
                // We check for lowercase 'unitSystem' first (common convention), then PascalCase 'UnitSystem'
                // We ensure the return type is an Enum to avoid picking up the nested Type definition itself
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

                // 3. If the member is instance-based, we need the Singleton instance (usually 'Instance')
                bool isStatic = false;
                if (unitSystemMember is PropertyInfo prop) isStatic = prop.GetGetMethod(true).IsStatic;
                else if (unitSystemMember is FieldInfo field) isStatic = field.IsStatic;

                if (unitSystemMember != null && !isStatic)
                {
                    var instProp = AccessTools.Property(psType, "Instance") ?? AccessTools.Property(psType, "instance");
                    if (instProp != null)
                    {
                        playerSettingsInstance = instProp.GetValue(null, null);
                    }
                }
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"Failed to initialize reflection for Unit Settings: {ex.Message}");
            }
        }

        private void ProcessMach()
        {
            float speedOfSound = LevelInfo.GetSpeedofSound(CurrentAircraft.transform.position.GlobalY());
            float currentMach = CurrentAircraft.speed / speedOfSound;

            if (SubsonicCallouts.Value)
            {
                if (prevMach >= 1.0f && currentMach < 1.0f)
                    PlaySound("subsonic");
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

                if (threshold == 2500 && style == AltCalloutStyle.British)
                {
                    clipName = "2500_UK";
                }

                PlaySound(clipName);
            }
        }

        public void PlaySound(string clipName)
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
                    audioSource.PlayOneShot(clip, MasterVolume.Value);
                }
                else
                {
                    Logger.LogWarning($"Sound '{clipName}' key exists but clip is null.");
                }
            }
            else
            {
                if (DebugMode.Value)
                {
                    Logger.LogWarning($"Sound '{clipName}' not found in cache.");
                    debugMessage += " (MISSING)";
                }
            }
        }

        // --- Setup & Loading ---

        private IEnumerator LoadSoundPack()
        {
            soundsLoaded = false;
            clipCache.Clear();

            string packName = SoundPack.Value;
            string basePath = Path.Combine(Paths.PluginPath, "AirDataAnnouncer", "Soundpacks");
            string path = Path.Combine(basePath, packName);

            if (!Directory.Exists(path))
            {
                Logger.LogError($"Soundpack not found at: {path}");
                if (Directory.Exists(basePath))
                {
                    var dirs = Directory.GetDirectories(basePath).Select(Path.GetFileName);
                    Logger.LogInfo($"Available packs: {string.Join(", ", dirs)}");
                }
                yield break;
            }

            Logger.LogInfo($"Loading sounds from: {path}");

            // "0" removed from list
            var filesToLoad = new List<string> {
                "10", "20", "30", "40", "50", "75", "100", "200", "300", "400", "500", "1000", "2500", "2500_UK", "subsonic",
                "Gear Down", "Gear Up", "Gear Down and Locked", "Gear Up and Locked", "contact"
            };

            for (int i = 1; i <= 10; i++) filesToLoad.Add($"Mach {i}");

            foreach (var filename in filesToLoad)
            {
                string filePath = Path.Combine(path, filename + ".wav");
                AudioType type = AudioType.WAV;

                if (!File.Exists(filePath))
                {
                    filePath = Path.Combine(path, filename + ".ogg");
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
                                clipCache[filename] = clip;
                            }
                        }
                    }
                }
                else
                {
                    if (DebugMode.Value && filename != "2500_UK") Logger.LogWarning($"File missing: {filename}");
                }
            }

            soundsLoaded = true;
            Logger.LogInfo($"Loaded {clipCache.Count} sounds successfully.");
        }

        private void SetupConfig()
        {
            // 1. General
            DebugMode = Config.Bind(SectionGeneral, "Debug Mode", false, "Show visual text on screen when sounds play");
            MasterVolume = Config.Bind(SectionGeneral, "Master Volume", 1.0f, new ConfigDescription("Volume of callouts", new AcceptableValueRange<float>(0f, 1f)));

            var soundPackPath = Path.Combine(Paths.PluginPath, "AirDataAnnouncer", "Soundpacks");
            var soundPacks = Directory.Exists(soundPackPath)
                ? Directory.GetDirectories(soundPackPath).Select(Path.GetFileName).ToArray()
                : new[] { "Altea" };

            SoundPack = Config.Bind(SectionGeneral, "Sound Pack", soundPacks.FirstOrDefault() ?? "Altea",
                new ConfigDescription("Select sound pack", new AcceptableValueList<string>(soundPacks)));

            SoundPack.SettingChanged += (sender, args) =>
            {
                if (this != null) StartCoroutine(LoadSoundPack());
            };

            // 2. Altitude Callouts
            // 0 removed
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

            // 3. Mach Callouts
            BindCallout(SectionMach, "Subsonic Callouts", "subsonic", ref SubsonicCallouts);
            MachCallouts.Clear();
            for (int i = 1; i <= 10; i++)
            {
                ConfigEntry<bool> entry = null;
                BindCallout(SectionMach, $"Mach {i} Callout", $"Mach {i}", ref entry);
                MachCallouts.Add(i, entry);
            }

            // 4. Gear Callouts
            BindCallout(SectionGear, "Gear Down (Action)", "Gear Down", ref GearDownCallout);
            BindCallout(SectionGear, "Gear Up (Action)", "Gear Up", ref GearUpCallout);
            BindCallout(SectionGear, "Gear Down & Locked", "Gear Down and Locked", ref GearDownLockedCallout);
            BindCallout(SectionGear, "Gear Up & Locked", "Gear Up and Locked", ref GearUpLockedCallout);
            BindCallout(SectionGear, "Contact (Weight On Wheels)", "contact", ref ContactCallout);
        }

        private void BindCallout(string section, string key, string audioClipName, ref ConfigEntry<bool> entry)
        {
            var description = new ConfigDescription(
                $"Enable {key}",
                null,
                new ConfigurationManagerAttributes
                {
                    CustomDrawer = (ConfigEntryBase e) =>
                    {
                        // Check if file exists in cache
                        bool exists = Instance != null && Instance.clipCache.ContainsKey(audioClipName);

                        GUI.enabled = exists;
                        if (GUILayout.Button("▶", GUILayout.Width(30))) Instance?.PlaySound(audioClipName);
                        GUI.enabled = true;

                        GUILayout.Space(10);
                        bool value = (bool)e.BoxedValue;

                        // Draw red text if missing
                        var originalColor = GUI.color;
                        if (!exists) GUI.color = Color.red;

                        bool newValue = GUILayout.Toggle(value, new GUIContent(key, exists ? null : "Sound file missing"));

                        if (!exists)
                        {
                            GUILayout.Label("(Missing)", GUILayout.ExpandWidth(false));
                        }

                        GUI.color = originalColor;

                        if (newValue != value) e.BoxedValue = newValue;
                    }
                }
            );
            entry = Config.Bind(section, key, true, description);
        }

        private void BindCallout2500(string section, string key, ref ConfigEntry<bool> entry)
        {
            Callout2500Style = Config.Bind(section, "2500 ft Style", AltCalloutStyle.American,
                new ConfigDescription("Style", null, new ConfigurationManagerAttributes { Browsable = false }));

            var description = new ConfigDescription(
                $"Enable {key}",
                null,
                new ConfigurationManagerAttributes
                {
                    CustomDrawer = (ConfigEntryBase e) =>
                    {
                        string clipToPlay = (Callout2500Style.Value == AltCalloutStyle.British) ? "2500_UK" : "2500";
                        bool exists = Instance != null && Instance.clipCache.ContainsKey(clipToPlay);

                        GUI.enabled = exists;
                        if (GUILayout.Button("▶", GUILayout.Width(30))) Instance?.PlaySound(clipToPlay);
                        GUI.enabled = true;

                        GUILayout.Space(10);
                        bool value = (bool)e.BoxedValue;

                        var originalColor = GUI.color;
                        if (!exists) GUI.color = Color.red;

                        bool newValue = GUILayout.Toggle(value, new GUIContent(key, exists ? null : "Sound file missing"));

                        if (!exists) GUILayout.Label("(Missing)", GUILayout.ExpandWidth(false));

                        GUI.color = originalColor;

                        if (newValue != value) e.BoxedValue = newValue;

                        GUILayout.Space(15);
                        GUILayout.Label("Style:");
                        GUILayout.Space(5);
                        int selected = (int)Callout2500Style.Value;
                        string[] options = { "US (2500)", "UK (2500)" };
                        int newSelected = GUILayout.Toolbar(selected, options, GUILayout.Width(150));
                        if (newSelected != selected) Callout2500Style.Value = (AltCalloutStyle)newSelected;
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
                // Cache landing gears for performance
                Instance.CurrentLandingGears = aircraft.GetComponentsInChildren<LandingGear>();
                Instance.prevGearDeployed = aircraft.gearDeployed;
                Instance.prevGearState = aircraft.gearState;
                Instance.prevRadarAlt = aircraft.radarAlt;
                Instance.wasGrounded = true;
                Instance.contactArmed = false; // Reset on new plane
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