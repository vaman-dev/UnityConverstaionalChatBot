using System;
using Convai.Editor.ConfigurationWindow.Services;
using Convai.Editor.Inspectors.Framework;
using Convai.Modules.Vision;
using Convai.Runtime;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Room;
using Convai.Runtime.Vision.Sources;
using Convai.Shared.Interfaces;
using Convai.Shared.Types;
using Convai.Editor.UI;
using UnityEditor;
using UnityEngine;
using Glyphs = Convai.Editor.UI.ConvaiEditorGlyphs;
using Theme = Convai.Editor.UI.ConvaiEditorTheme;

namespace Convai.Editor.Inspectors
{
    /// <summary>
    ///     Convai editor for <see cref="ConvaiRoomManager" />: how the player talks, how the scene
    ///     connects, a rolling scene setup health report, and three collapsed-by-default sections
    ///     holding the room source, the advanced room control and the runtime readout.
    /// </summary>
    [CustomEditor(typeof(ConvaiRoomManager))]
    internal sealed class ConvaiRoomManagerEditor : ConvaiInspectorEditor
    {
        private const string TitleText = "Convai Room Manager";

        private const string PurposeText =
            "Owns the conversation room for this scene — connection, turn-taking and reconnect behavior.";

        private const string SectionConfigurationId = "Configuration";
        private const string SectionRoomDefaultsId = "RoomDefaults";
        private const string SectionRuntimeId = "Runtime";
        private const string SectionConversationId = "Conversation";

        /// <remarks>
        ///     Kept as "Scene" so a project that already collapsed or expanded this section keeps its
        ///     choice across the rename to Connection. The id is storage, not copy.
        /// </remarks>
        private const string SectionConnectionId = "Scene";

        private const string SectionValidationId = "Validation";

        private static readonly GUIContent ConversationSection = new("Conversation");
        private static readonly GUIContent ConnectionSection = new("Connection");
        private static readonly GUIContent ValidationSection = new("Validation");
        private static readonly GUIContent RoomSourceSection = new("Room Source");
        private static readonly GUIContent RoomControlSection = new("Advanced Room Control");
        private static readonly GUIContent RuntimeSection = new("Runtime");

        private static readonly GUIContent PushToTalkChip = new("Push To Talk");
        private static readonly GUIContent HandsFreeChip = new("Hands Free");

        private static readonly GUIContent AddVisionComponentsButton = new("Add Missing Components");

        private static readonly GUIContent RunValidationButton = new(
            "Run Full Validation", "Re-run every check now and report the result in the Console.");

        private static readonly GUIContent OpenProjectSettingsButton = new(
            "Project Settings", "Open Project Settings \u2192 Convai SDK, where the API key and server live.");

        private static readonly GUIContent ClearLegacyOverrideButton = new(
            "Clear Saved URL", "Remove the unused server URL this older scene still stores.");

        private static readonly GUIContent OpenSdkSettingsButton = new(
            "Convai SDK Settings", "Open Project Settings \u2192 Convai SDK.");

        private static readonly GUIContent SessionGroup = new("Session");
        private static readonly GUIContent InEffectGroup = new("Settings In Effect");
        private static readonly GUIContent WiringGroup = new("Wiring");

        private static readonly GUIContent SessionStateLabel = new(
            "Session State", "Where this room is in its connect, run and disconnect cycle.");

        private static readonly GUIContent ConnectedLabel = new(
            "Connected", "Whether the room connection is open right now.");

        private static readonly GUIContent CurrentRoomLabel = new("Room", "The room this scene has joined.");

        private static readonly GUIContent SessionIdLabel = new(
            "Session ID", "Identifies this conversation to the service. Quote it in a support request.");

        private static readonly GUIContent CoreServerLabel = new(
            "Core Server", "Taken from Project Settings \u2192 Convai SDK, for every scene in the project.");

        private static readonly GUIContent VisionContextLabel = new(
            "Dynamic Vision Context", "Whether the character is being sent camera frames, and under which rule.");

        private static readonly GUIContent EffectiveConnectionLabel = new(
            "Connection Type", "The connection this room will actually open, once a profile has had its say.");

        private static readonly GUIContent RoomControllerLabel = new(
            "Room Controller", "The implementation driving this room. Diagnostic.");

        private static readonly GUIContent TransportLabel = new(
            "Transport", "The transport carrying this room's audio and messages. Diagnostic.");

        private const string NotConnectedValue = "Not connected";

        /// <summary>Height of one validation check's title row.</summary>
        private const float CheckRowHeight = 18f;

        /// <summary>Width of the column the status dot sits in, left of a check's title.</summary>
        private const float CheckDotColumn = 14f;

        private SerializedProperty _autoMicStartDelaySecondsProp;
        private SetupHealthReport _cachedSetupHealthReport;
        private SerializedProperty _configurationSourceInitializedProp;
        private SerializedProperty _configurationSourceProp;
        private SerializedProperty _connectionTypeProp;
        private SerializedProperty _connectOnStartProp;
        private SerializedProperty _coreServerBaseUrlProp;
        private SerializedProperty _debugProp;
        private SerializedProperty _maxReconnectAttemptsProp;
        private ConvaiEditorRefreshTimer _validationTimer;
        private SerializedProperty _resumePolicyProp;
        private SerializedProperty _roomConfigAssetProp;
        private SerializedObject _roomConfigSerializedObject;
        private ConvaiRoomManager _roomManager;
        private SerializedProperty _roomPushToTalkKeyProp;
        private SerializedProperty _roomRejoinTtlSecondsProp;
        private SerializedProperty _serverEndpointProp;
        private SerializedProperty _spawnAgentOnRejoinProp;
        private SerializedProperty _startWaitTimeoutMsProp;
        private SerializedProperty _turnTakingOptionsProp;
        private SerializedProperty _userVadSettingsProp;
        private SerializedProperty _visionContextModeProp;
        private SerializedProperty _visionInputSettingsProp;
        private SerializedProperty _visionRespondModesProp;

        protected override string Title => TitleText;
        protected override string Purpose => PurposeText;

        protected override GUIContent StatusChip =>
            _roomManager != null &&
            _roomManager.EffectiveTurnTakingOptions.Mode == ConversationInputMode.PushToTalk
                ? PushToTalkChip
                : HandsFreeChip;

        protected override Color StatusChipTint => Theme.StatusInfo;

        private bool IsAssetModeSelected =>
            _configurationSourceProp != null &&
            (ConvaiConfigSourceMode)_configurationSourceProp.enumValueIndex == ConvaiConfigSourceMode.Asset;

        private bool HasLegacyCoreServerOverride =>
            _coreServerBaseUrlProp != null && !string.IsNullOrWhiteSpace(_coreServerBaseUrlProp.stringValue);

        protected override void OnEnable()
        {
            base.OnEnable();

            _roomManager = (ConvaiRoomManager)target;

            _configurationSourceProp = serializedObject.FindProperty("_configurationSource");
            _configurationSourceInitializedProp = serializedObject.FindProperty("_configurationSourceInitialized");
            _roomConfigAssetProp = serializedObject.FindProperty("_roomConfigAsset");
            _connectionTypeProp = serializedObject.FindProperty("_connectionType");
            _coreServerBaseUrlProp = serializedObject.FindProperty("<CoreServerBaseURL>k__BackingField");
            _serverEndpointProp = serializedObject.FindProperty("<ServerEndpoint>k__BackingField");
            _connectOnStartProp = serializedObject.FindProperty("<ConnectOnStart>k__BackingField");
            _turnTakingOptionsProp = serializedObject.FindProperty("_turnTakingOptions");
            _userVadSettingsProp = serializedObject.FindProperty("_userVadSettings");
            _visionContextModeProp = serializedObject.FindProperty("_visionContextMode");
            _visionInputSettingsProp = serializedObject.FindProperty("_visionInputSettings");
            _visionRespondModesProp = serializedObject.FindProperty("_visionRespondModes");
            _roomPushToTalkKeyProp = serializedObject.FindProperty("_pushToTalkKey");
            _debugProp = serializedObject.FindProperty("<Debug>k__BackingField");
            _roomRejoinTtlSecondsProp = serializedObject.FindProperty("_roomRejoinTtlSeconds");
            _resumePolicyProp = serializedObject.FindProperty("_resumePolicy");
            _maxReconnectAttemptsProp = serializedObject.FindProperty("_maxReconnectAttempts");
            _spawnAgentOnRejoinProp = serializedObject.FindProperty("_spawnAgentOnRejoin");
            _startWaitTimeoutMsProp = serializedObject.FindProperty("_startWaitTimeoutMs");
            _autoMicStartDelaySecondsProp = serializedObject.FindProperty("_autoMicStartDelaySeconds");

            EnsureConfigurationSourceMigration();
        }

        protected override void OnBeforeInspectorGUI() => EnsureConfigurationSourceMigration();

        /// <summary>Keeps the Runtime section live.</summary>
        /// <remarks>
        ///     Everything that section reports moves without anyone touching the Inspector: the
        ///     session state, whether the room is connected, the room and session it joined. Without
        ///     this it shows whatever was true at the last repaint, and a stale live reading is worse
        ///     than none — it cannot be told apart from a current one.
        /// </remarks>
        public override bool RequiresConstantRepaint() => EditorApplication.isPlaying;

        protected override void DrawBody()
        {
            // Without the source/connection properties there is no bespoke page left to draw, so fall
            // through to the attribute-driven renderer rather than showing an empty inspector.
            if (!HasRequiredProperties())
            {
                DrawGeneratedSections();
                return;
            }

            DrawConversationSection();
            DrawConnectionSection();
            DrawValidationSection();

            // The three advanced sections sit flat alongside the everyday ones rather than nested
            // inside an "Advanced" wrapper: they start collapsed, so they stay out of the way, and a
            // flat stack of cards is what every other Convai inspector looks like. Nesting them put
            // a card inside a card inside a panel, which read as a rendering fault.
            DrawRoomSourceSection();
            DrawRoomControlSection();
            DrawRuntimeSection();
        }

        private bool HasRequiredProperties() =>
            _configurationSourceProp != null &&
            _roomConfigAssetProp != null &&
            _connectionTypeProp != null &&
            _connectOnStartProp != null;

        private void EnsureConfigurationSourceMigration()
        {
            if (_configurationSourceInitializedProp == null || _roomConfigAssetProp == null ||
                _configurationSourceProp == null)
                return;

            if (_configurationSourceInitializedProp.boolValue) return;

            _configurationSourceProp.enumValueIndex =
                _roomConfigAssetProp.objectReferenceValue != null
                    ? (int)ConvaiConfigSourceMode.Asset
                    : (int)ConvaiConfigSourceMode.Inline;
            _configurationSourceInitializedProp.boolValue = true;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
            serializedObject.Update();
        }

        private void DrawConversationSection()
        {
            if (!DrawSection(SectionConversationId, ConversationSection, Glyphs.Content)) return;
            DrawSectionBody(() =>
            {
                SerializedProperty turnTakingOptionsProp =
                    GetConversationTurnTakingOptionsProperty(out bool readOnlyAssetValues);
                if (turnTakingOptionsProp == null)
                {
                    WarningBox(
                        "Turn-taking unavailable",
                        IsAssetModeSelected
                            ? "Assign a Room Manager Profile asset or switch Room Setup Source back to scene defaults."
                            : "This Room Manager's saved data does not contain turn-taking settings. "
                              + "Remove the Convai Room Manager component and add it again to rebuild them.");
                    return;
                }

                if (readOnlyAssetValues && _roomConfigAssetProp.objectReferenceValue != null)
                    GUILayout.Label(
                        $"Using Room Manager Profile: {_roomConfigAssetProp.objectReferenceValue.name}",
                        Theme.MutedWrapped);

                SerializedProperty modeProp = turnTakingOptionsProp.FindPropertyRelative("<Mode>k__BackingField");
                SerializedProperty pushToTalkPolicyProp =
                    turnTakingOptionsProp.FindPropertyRelative("<PushToTalkPolicy>k__BackingField");
                if (modeProp == null || pushToTalkPolicyProp == null)
                    return;

                using (new EditorGUI.DisabledScope(readOnlyAssetValues))
                {
                    EditorGUILayout.PropertyField(modeProp, ConvaiInspectorContent.HowThePlayerTalks);
                }

                if ((ConversationInputMode)modeProp.enumValueIndex != ConversationInputMode.PushToTalk)
                    return;

                if (_roomPushToTalkKeyProp != null)
                    EditorGUILayout.PropertyField(_roomPushToTalkKeyProp, ConvaiInspectorContent.PushToTalkKey);

                using (new EditorGUI.DisabledScope(readOnlyAssetValues))
                {
                    EditorGUILayout.PropertyField(
                        pushToTalkPolicyProp.FindPropertyRelative("<InterruptBotOnPress>k__BackingField"),
                        ConvaiInspectorContent.InterruptCharacterWhenPressed);
                    EditorGUILayout.PropertyField(
                        pushToTalkPolicyProp.FindPropertyRelative(
                            "<RequireTurnCompletionBeforeNextPress>k__BackingField"),
                        ConvaiInspectorContent.WaitForCharacterToFinishBeforeTalkingAgain);
                    EditorGUILayout.PropertyField(
                        pushToTalkPolicyProp.FindPropertyRelative("<TurnCompletionTimeoutMs>k__BackingField"),
                        ConvaiInspectorContent.FallbackWaitTimeMs);
                }
            });
        }

        /// <summary>
        ///     The two decisions a scene makes about connecting, drawn together at the top.
        /// </summary>
        /// <remarks>
        ///     Connection Type decides whether the character can see the scene at all, which makes it an
        ///     ordinary first-day setup choice rather than advanced tuning — so it sits here, in the
        ///     open, and not inside the collapsed advanced section where a reader would have to already
        ///     know it existed. Advanced Room Control deliberately does not also offer it: a setting
        ///     editable in two places is a setting a user can disagree with themselves about.
        /// </remarks>
        private void DrawConnectionSection()
        {
            if (!DrawSection(SectionConnectionId, ConnectionSection, Glyphs.Routing)) return;
            DrawSectionBody(() =>
            {
                SerializedProperty connectionTypeProp = GetConnectionTypeProperty(out bool readOnlyAssetValue);
                SerializedProperty connectOnStartProp = GetConnectOnStartProperty(out _);
                if (connectionTypeProp == null && connectOnStartProp == null)
                {
                    WarningBox(
                        "Connection settings unavailable",
                        IsAssetModeSelected
                            ? "Assign a Room Manager Profile asset or switch Room Setup Source back to scene defaults."
                            : "This Room Manager's saved data does not contain connection settings. "
                              + "Remove the Convai Room Manager component and add it again to rebuild them.");
                    return;
                }

                if (readOnlyAssetValue && _roomConfigAssetProp.objectReferenceValue != null)
                    GUILayout.Label(
                        $"These values come from Room Manager Profile: {_roomConfigAssetProp.objectReferenceValue.name}",
                        Theme.MutedWrapped);

                using (new EditorGUI.DisabledScope(readOnlyAssetValue))
                {
                    if (connectionTypeProp != null)
                        EditorGUILayout.PropertyField(connectionTypeProp, ConvaiInspectorContent.ConnectionType);

                    if (connectOnStartProp != null)
                        EditorGUILayout.PropertyField(connectOnStartProp, ConvaiInspectorContent.StartsConnected);
                }

                DrawVideoRequirementsHint(connectionTypeProp);
            });
        }

        /// <summary>
        ///     Video is the only connection type with extra scene requirements, so what it needs is named
        ///     — and offered — where the choice is made, instead of only in Validation further down.
        /// </summary>
        private void DrawVideoRequirementsHint(SerializedProperty connectionTypeProp)
        {
            if (connectionTypeProp == null ||
                (ConvaiConnectionType)connectionTypeProp.enumValueIndex != ConvaiConnectionType.Video)
                return;

            (bool hasPublisher, bool hasFrameSource) = GetVisionComponentFlags();
            if (hasPublisher && hasFrameSource)
                return;

            WarningBox(
                "Video needs a camera feed",
                $"A Video connection sends camera frames to the character, which needs " +
                $"{GetMissingVideoRequirementsMessage(hasPublisher, hasFrameSource)} on this GameObject or a " +
                "child. Until then the character connects but sees nothing.",
                AddVisionComponentsButton.text,
                () => AddMissingVisionComponents(hasPublisher, hasFrameSource));
        }

        /// <summary>
        ///     Adds whichever of the two vision components is missing, on the Room Manager's own
        ///     GameObject, in one undoable step.
        /// </summary>
        /// <remarks>
        ///     <see cref="CameraVisionFrameSource" /> is the frame source to default to: it renders from a
        ///     scene camera, so it needs no device permission and no XR package, and a project that wants
        ///     the webcam or a Quest passthrough feed can swap the component afterwards.
        /// </remarks>
        private void AddMissingVisionComponents(bool hasPublisher, bool hasFrameSource)
        {
            if (_roomManager == null)
                return;

            GameObject host = _roomManager.gameObject;

            if (!hasPublisher)
                Undo.AddComponent<ConvaiVisionPublisher>(host);

            if (!hasFrameSource)
                Undo.AddComponent<CameraVisionFrameSource>(host);

            InvalidateValidationCache(true);
            EditorUtility.SetDirty(host);
        }

        /// <summary>
        ///     The scene's setup checks, coloured by what they actually found.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The header used to be permanently amber, so a scene where every check passed still
        ///         wore a warning badge — the one colour in the inspector that means "look here"
        ///         spent on the case where there is nothing to look at. It now takes the colour of
        ///         the worst check, and carries the verdict as a summary so a collapsed section still
        ///         reports it.
        ///     </para>
        ///     <para>
        ///         A healthy scene gets no message box either. "Scene setup / Scene setup looks
        ///         healthy" said the same thing twice and then repeated it a third time in the ticks
        ///         below. The box now appears only when something is wrong, which is what makes a box
        ///         worth noticing.
        ///     </para>
        /// </remarks>
        private void DrawValidationSection()
        {
            SetupHealthReport report = GetSetupHealthReport();
            bool blocked = report.HasBlockingIssues;
            bool warned = report.HasWarnings || HasRoomSpecificIssues();

            Color accent = blocked ? Theme.StatusError : warned ? Theme.StatusWarn : Theme.StatusReady;
            string summary = blocked ? "Needs fixing" : warned ? "Review" : "All checks passed";

            if (!DrawSection(
                    SectionValidationId, ValidationSection, Glyphs.Validation, accent: accent, summary: summary))
                return;

            DrawSectionBody(() =>
            {
                if (blocked)
                    ErrorBox(
                        "Setup incomplete",
                        "Something this scene needs is still missing. The checks below name it.");
                else if (warned)
                    WarningBox(
                        "Almost ready",
                        "Nothing here stops the scene running, but the checks below are worth a look.");

                foreach (SetupHealthCheckResult result in report.Results)
                    DrawCheckRow(result.Status, result.Title, result.Message);

                DrawRoomSpecificValidationMessages();

                GUILayout.Space(2f);
                DrawActionRow(
                    RunValidationButton,
                    () =>
                    {
                        InvalidateValidationCache(true);
                        ConvaiSetupWizard.ValidateSceneSetup();
                    },
                    OpenProjectSettingsButton,
                    () => SettingsService.OpenProjectSettings("Project/Convai SDK"));
            });
        }

        /// <summary>
        ///     One check: a status dot, its title, the word for anything that is not healthy, and the
        ///     sentence underneath.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The title used to be drawn in the <i>section title</i> style, so four checks read
        ///         as four section headings competing with the real one above them, and the tick
        ///         carrying the verdict was a character in that same text — the same weight and the
        ///         same colour whether the check had passed or failed.
        ///     </para>
        ///     <para>
        ///         A healthy check says nothing on the right. The dot has already said it, and a
        ///         column of the word "Healthy" is a column that teaches the eye to skip the place
        ///         where "Blocked" will appear.
        ///     </para>
        /// </remarks>
        private static void DrawCheckRow(SetupHealthStatus status, string title, string message)
        {
            Color tint = StatusTint(status);
            bool healthy = status == SetupHealthStatus.Healthy;

            Rect row = GUILayoutUtility.GetRect(0f, CheckRowHeight, GUILayout.ExpandWidth(true));
            Theme.StatusDot(new Rect(row.x, row.y, CheckDotColumn, row.height), tint, !healthy);

            string statusWord = healthy ? string.Empty : StatusWord(status);
            float statusWidth = healthy
                ? 0f
                : Mathf.Min(Theme.TextWidth(Theme.MicroLabelRight, statusWord) + 8f, row.width * 0.4f);

            GUI.Label(
                new Rect(
                    row.x + CheckDotColumn,
                    row.y,
                    Mathf.Max(0f, row.width - CheckDotColumn - statusWidth),
                    row.height),
                title,
                Theme.RowLabel);

            if (statusWidth > 0f)
                GUI.Label(
                    new Rect(row.xMax - statusWidth, row.y, statusWidth, row.height),
                    statusWord,
                    Theme.MicroLabelRightTinted(tint));

            if (!string.IsNullOrWhiteSpace(message))
                GUILayout.Label(message, Theme.MutedWrapped);

            GUILayout.Space(6f);
        }

        /// <summary>
        ///     A row of section actions, each sized to its own label.
        /// </summary>
        /// <remarks>
        ///     These were Unity mini buttons pinned to a hand-measured 160 pixels, which is both a
        ///     different button from every other button in the product and a width that clips one
        ///     label and leaves another swimming. Ghost buttons at their measured width are the
        ///     design system's answer, and they read as the secondary actions they are.
        /// </remarks>
        private static void DrawActionRow(
            GUIContent firstLabel, Action first, GUIContent secondLabel = null, Action second = null)
        {
            float firstWidth = Theme.GhostButtonWidth(firstLabel);
            float secondWidth = secondLabel != null ? Theme.GhostButtonWidth(secondLabel) : 0f;

            Rect row = GUILayoutUtility.GetRect(0f, 22f, GUILayout.ExpandWidth(true));
            float x = row.x + Theme.ContentInset;

            if (Theme.GhostButton(new Rect(x, row.y, firstWidth, row.height), firstLabel))
                first?.Invoke();

            if (secondLabel == null) return;

            if (Theme.GhostButton(
                    new Rect(x + firstWidth + 6f, row.y, secondWidth, row.height), secondLabel))
                second?.Invoke();
        }

        private void DrawRoomSourceSection()
        {
            if (!DrawSection(SectionConfigurationId, RoomSourceSection, Glyphs.Profile, defaultExpanded: false))
                return;
            DrawSectionBody(() =>
            {
                EditorGUILayout.PropertyField(_configurationSourceProp, ConvaiInspectorContent.RoomSetupSource);
                if (IsAssetModeSelected)
                    EditorGUILayout.PropertyField(_roomConfigAssetProp, ConvaiInspectorContent.RoomConfigAsset);

                GUILayout.Label(GetRoomSourceStatusText(), Theme.MutedWrapped);
                if (IsAssetModeSelected && _roomConfigAssetProp.objectReferenceValue == null)
                    WarningBox(
                        "No profile assigned",
                        "Assign a Room Manager Profile asset or switch Room Setup Source back to scene defaults.");
            });
        }

        private void DrawRoomControlSection()
        {
            if (!DrawSection(SectionRoomDefaultsId, RoomControlSection, Glyphs.Section, defaultExpanded: false))
                return;
            DrawSectionBody(() =>
            {
                if (IsAssetModeSelected && _roomConfigAssetProp.objectReferenceValue == null)
                {
                    WarningBox(
                        "No profile assigned",
                        "Assign a Room Manager Profile asset to review advanced room control while Room Setup " +
                        "Source is set to Room Manager Profile Asset.");
                    return;
                }

                if (IsAssetModeSelected)
                    GUILayout.Label(
                        $"These values come from Room Manager Profile: {_roomConfigAssetProp.objectReferenceValue.name}. " +
                        "Edit the asset to change them.",
                        Theme.MutedWrapped);

                ConvaiRoomControlInspectorView.Draw(
                    BuildRoomControlProperties(out bool readOnlyAssetValues),
                    readOnlyAssetValues);
            });
        }

        /// <summary>
        ///     Collects the properties Advanced Room Control draws, from whichever object owns them.
        /// </summary>
        /// <remarks>
        ///     In asset mode the values live on the Room Manager Profile and are shown read-only, so the
        ///     component's inspector reports the room it will actually join rather than the fields it
        ///     happens to serialize.
        /// </remarks>
        private ConvaiRoomControlProperties BuildRoomControlProperties(out bool readOnlyAssetValues)
        {
            readOnlyAssetValues = false;

            if (!IsAssetModeSelected)
                return new ConvaiRoomControlProperties
                {
                    ConnectionTypeForDisplay = _connectionTypeProp,
                    ServerEndpoint = _serverEndpointProp,
                    TurnTakingOptions = _turnTakingOptionsProp,
                    UserVadSettings = _userVadSettingsProp,
                    VisionContextMode = _visionContextModeProp,
                    VisionInputSettings = _visionInputSettingsProp,
                    VisionRespondModes = _visionRespondModesProp,
                    RoomRejoinTtlSeconds = _roomRejoinTtlSecondsProp,
                    ResumePolicy = _resumePolicyProp,
                    MaxReconnectAttempts = _maxReconnectAttemptsProp,
                    SpawnAgentOnRejoin = _spawnAgentOnRejoinProp,
                    StartWaitTimeoutMs = _startWaitTimeoutMsProp,
                    AutoMicStartDelaySeconds = _autoMicStartDelaySecondsProp
                };

            if (_roomConfigAssetProp.objectReferenceValue is not ConvaiRoomManagerProfile roomConfig)
                return null;

            SerializedObject asset = GetRoomConfigSerializedObject(roomConfig);
            if (asset == null)
                return null;

            readOnlyAssetValues = true;
            return new ConvaiRoomControlProperties
            {
                ConnectionTypeForDisplay = asset.FindProperty("_connectionType"),
                VideoTrackName = asset.FindProperty("_videoTrackName"),
                ServerEndpoint = asset.FindProperty("_serverEndpoint"),
                TurnTakingOptions = asset.FindProperty("_turnTakingOptions"),
                UserVadSettings = asset.FindProperty("_userVadSettings"),
                VisionContextMode = asset.FindProperty("_visionContextMode"),
                VisionInputSettings = asset.FindProperty("_visionInputSettings"),
                VisionRespondModes = asset.FindProperty("_visionRespondModes"),
                RoomRejoinTtlSeconds = asset.FindProperty("_roomRejoinTtlSeconds"),
                ResumePolicy = asset.FindProperty("_resumePolicy"),
                MaxReconnectAttempts = asset.FindProperty("_maxReconnectAttempts"),
                SpawnAgentOnRejoin = asset.FindProperty("_spawnAgentOnRejoin"),
                StartWaitTimeoutMs = asset.FindProperty("_startWaitTimeoutMs"),
                AutoMicStartDelaySeconds = asset.FindProperty("_autoMicStartDelaySeconds")
            };
        }

        private void DrawRuntimeSection()
        {
            if (!DrawSection(
                    SectionRuntimeId, RuntimeSection, Glyphs.Live, defaultExpanded: false, accent: Theme.StatusInfo))
                return;
            DrawSectionBody(() =>
            {
                string projectServerUrl =
                    ConvaiSettings.Instance != null ? ConvaiSettings.Instance.ServerUrl : string.Empty;
                string legacyOverride = _coreServerBaseUrlProp?.stringValue ?? string.Empty;
                bool hasLegacyOverride = !string.IsNullOrWhiteSpace(legacyOverride);
                bool connected = _roomManager.IsConnected;

                // Readings, not fields. These were nine disabled text boxes, which is the shape of a
                // form somebody has greyed out — a reader tries to type in them. They are the same
                // two-column readout the Convai Manager's Live section uses, so the two inspectors
                // report a running scene in one voice.
                Theme.GroupCaption(SessionGroup);
                Theme.KeyValueRow(
                    SessionStateLabel,
                    ObjectNames.NicifyVariableName(_roomManager.CurrentState.ToString()),
                    connected ? Theme.StatusReady : Theme.TextMuted);
                Theme.KeyValueRow(
                    ConnectedLabel,
                    connected ? "Yes" : "No",
                    connected ? Theme.StatusReady : Theme.TextMuted);
                Theme.KeyValueRow(CurrentRoomLabel, ValueOrNotConnected(_roomManager.CurrentRoomName));
                Theme.KeyValueRow(SessionIdLabel, ValueOrNotConnected(_roomManager.CurrentSessionId));

                GUILayout.Space(6f);
                Theme.GroupCaption(InEffectGroup);
                Theme.KeyValueRow(
                    EffectiveConnectionLabel, _roomManager.EffectiveConnectionType.ToString());
                Theme.KeyValueRow(
                    VisionContextLabel,
                    _roomManager.EffectiveVisionContextEnabled
                        ? $"{_roomManager.EffectiveVisionContextMode} — sending frames"
                        : $"{_roomManager.EffectiveVisionContextMode} — off",
                    _roomManager.EffectiveVisionContextEnabled ? Theme.StatusInfo : Theme.TextMuted);
                Theme.KeyValueRow(
                    CoreServerLabel,
                    string.IsNullOrWhiteSpace(projectServerUrl) ? "Not set" : projectServerUrl);

                bool hasWiring =
                    !string.IsNullOrWhiteSpace(_roomManager.RoomControllerTypeName) ||
                    !string.IsNullOrWhiteSpace(_roomManager.TransportAccessorTypeName);
                if (hasWiring)
                {
                    GUILayout.Space(6f);
                    Theme.GroupCaption(WiringGroup);
                    if (!string.IsNullOrWhiteSpace(_roomManager.RoomControllerTypeName))
                        Theme.KeyValueRow(RoomControllerLabel, _roomManager.RoomControllerTypeName);
                    if (!string.IsNullOrWhiteSpace(_roomManager.TransportAccessorTypeName))
                        Theme.KeyValueRow(TransportLabel, _roomManager.TransportAccessorTypeName);
                }

                if (hasLegacyOverride)
                {
                    GUILayout.Space(6f);
                    InfoBox(
                        "A saved server URL here is ignored",
                        $"This older scene still stores '{legacyOverride.Trim()}'. Every connection now uses the " +
                        "Core Server above, which comes from Project Settings. Clearing the saved value tidies " +
                        "the scene and changes nothing.");
                }

                GUILayout.Space(4f);
                if (hasLegacyOverride)
                    DrawActionRow(
                        ClearLegacyOverrideButton,
                        ClearLegacyCoreServerOverride,
                        OpenSdkSettingsButton,
                        () => SettingsService.OpenProjectSettings("Project/Convai SDK"));
                else
                    DrawActionRow(
                        OpenSdkSettingsButton,
                        () => SettingsService.OpenProjectSettings("Project/Convai SDK"));

                Theme.HorizontalRule(Theme.Divider);
                EditorGUILayout.PropertyField(_debugProp, ConvaiInspectorContent.Debug);
            });
        }

        /// <summary>A live reading, or the one phrase that explains every blank one at once.</summary>
        private static string ValueOrNotConnected(string value) =>
            string.IsNullOrWhiteSpace(value) ? NotConnectedValue : value;

        private string GetRoomSourceStatusText()
        {
            if (!IsAssetModeSelected)
                return "Using scene defaults.";

            return _roomConfigAssetProp.objectReferenceValue != null
                ? $"Using Room Manager Profile: {_roomConfigAssetProp.objectReferenceValue.name}"
                : "Room Manager Profile not assigned.";
        }

        private SerializedProperty GetConversationTurnTakingOptionsProperty(out bool readOnlyAssetValues)
        {
            readOnlyAssetValues = false;
            if (!IsAssetModeSelected)
                return _turnTakingOptionsProp;

            if (_roomConfigAssetProp.objectReferenceValue is not ConvaiRoomManagerProfile roomConfig)
                return null;

            SerializedObject roomConfigSerializedObject = GetRoomConfigSerializedObject(roomConfig);
            if (roomConfigSerializedObject == null)
                return null;

            readOnlyAssetValues = true;
            return roomConfigSerializedObject.FindProperty("_turnTakingOptions");
        }

        private SerializedProperty GetConnectionTypeProperty(out bool readOnlyAssetValue)
        {
            readOnlyAssetValue = false;
            if (!IsAssetModeSelected)
                return _connectionTypeProp;

            if (_roomConfigAssetProp.objectReferenceValue is not ConvaiRoomManagerProfile roomConfig)
                return null;

            SerializedObject roomConfigSerializedObject = GetRoomConfigSerializedObject(roomConfig);
            if (roomConfigSerializedObject == null)
                return null;

            readOnlyAssetValue = true;
            return roomConfigSerializedObject.FindProperty("_connectionType");
        }

        private SerializedProperty GetConnectOnStartProperty(out bool readOnlyAssetValue)
        {
            readOnlyAssetValue = false;
            if (!IsAssetModeSelected)
                return _connectOnStartProp;

            if (_roomConfigAssetProp.objectReferenceValue is not ConvaiRoomManagerProfile roomConfig)
                return null;

            SerializedObject roomConfigSerializedObject = GetRoomConfigSerializedObject(roomConfig);
            if (roomConfigSerializedObject == null)
                return null;

            readOnlyAssetValue = true;
            return roomConfigSerializedObject.FindProperty("_connectOnStart");
        }

        private SerializedObject GetRoomConfigSerializedObject(ConvaiRoomManagerProfile roomConfig)
        {
            if (roomConfig == null)
            {
                _roomConfigSerializedObject = null;
                return null;
            }

            if (_roomConfigSerializedObject == null || _roomConfigSerializedObject.targetObject != roomConfig)
                _roomConfigSerializedObject = new SerializedObject(roomConfig);

            _roomConfigSerializedObject.UpdateIfRequiredOrScript();
            return _roomConfigSerializedObject;
        }

        private bool HasRoomSpecificIssues()
        {
            if (IsAssetModeSelected && _roomConfigAssetProp.objectReferenceValue == null)
                return true;

            if (_roomManager != null && _roomManager.EffectiveConnectionType == ConvaiConnectionType.Video)
            {
                (bool hasPublisher, bool hasFrameSource) = GetVisionComponentFlags();
                if (!hasPublisher || !hasFrameSource)
                    return true;
            }

            return false;
        }

        private void DrawRoomSpecificValidationMessages()
        {
            bool anyRoomSpecificIssues = false;

            if (IsAssetModeSelected && _roomConfigAssetProp.objectReferenceValue == null)
            {
                // A warning, not a blocker, and the distinction is measurable:
                // ConvaiRoomManager.UsesRoomConfigAsset is `mode == Asset && _roomConfigAsset != null`,
                // so an unassigned profile does not stop the room — every Effective* value quietly
                // falls back to the inline ones. Reporting it as Blocked contradicted the section's
                // own summary, which correctly said Review and that nothing here stops the scene.
                // The silent fallback is the part worth saying out loud.
                DrawCheckRow(
                    SetupHealthStatus.Warning,
                    "Room Manager Profile",
                    "Room Setup Source is set to Room Manager Profile Asset, but no profile is assigned. " +
                    "The room still connects — it quietly uses this component's own settings instead, " +
                    "so the profile you meant to apply is not being applied.");
                anyRoomSpecificIssues = true;
            }

            if (_roomManager != null && _roomManager.EffectiveConnectionType == ConvaiConnectionType.Video)
            {
                (bool hasPublisher, bool hasFrameSource) = GetVisionComponentFlags();
                if (!hasPublisher || !hasFrameSource)
                {
                    DrawCheckRow(
                        SetupHealthStatus.Warning,
                        "Dynamic Vision Requirements",
                        "Dynamic vision context is missing " +
                        $"{GetMissingVideoRequirementsMessage(hasPublisher, hasFrameSource)}.");
                    anyRoomSpecificIssues = true;
                }
            }

            // Nothing is said when there is nothing to say. "No room-specific issues detected" sat
            // under the last check with no separation, so it read as that check's own message —
            // attributing one subject's verdict to another, which is the one thing a list of checks
            // must never do.
            _ = anyRoomSpecificIssues;
        }

        /// <summary>The colour a check's dot and status word take.</summary>
        private static Color StatusTint(SetupHealthStatus status) =>
            status switch
            {
                SetupHealthStatus.Healthy => Theme.StatusReady,
                SetupHealthStatus.Warning => Theme.StatusWarn,
                _ => Theme.StatusError
            };

        /// <summary>
        ///     The verdict in a word, so the row does not rely on colour alone to carry it.
        /// </summary>
        private static string StatusWord(SetupHealthStatus status) =>
            status switch
            {
                SetupHealthStatus.Healthy => "Ready",
                SetupHealthStatus.Warning => "Check this",
                _ => "Blocked"
            };

        private SetupHealthReport GetSetupHealthReport()
        {
            if (_validationTimer.ShouldRefresh(_cachedSetupHealthReport != null))
                _cachedSetupHealthReport = SetupHealthService.BuildReport();

            return _cachedSetupHealthReport;
        }

        private void InvalidateValidationCache(bool forceImmediateRefresh = false)
        {
            _cachedSetupHealthReport = null;
            _validationTimer.Invalidate(forceImmediateRefresh);
        }

        private (bool hasPublisher, bool hasFrameSource) GetVisionComponentFlags()
        {
            bool hasPublisher = false;
            bool hasFrameSource = false;

            foreach (MonoBehaviour component in _roomManager.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!hasPublisher && component is IVisionPublisher) hasPublisher = true;
                if (!hasFrameSource && component is IVisionFrameSource) hasFrameSource = true;
                if (hasPublisher && hasFrameSource) break;
            }

            return (hasPublisher, hasFrameSource);
        }

        private static string GetMissingVideoRequirementsMessage(bool hasPublisher, bool hasFrameSource)
        {
            if (!hasPublisher && !hasFrameSource)
                return "ConvaiVisionPublisher and a vision frame source";
            if (!hasPublisher)
                return "ConvaiVisionPublisher";
            return "a vision frame source";
        }

        private void ClearLegacyCoreServerOverride()
        {
            if (!HasLegacyCoreServerOverride) return;

            Undo.RecordObject(_roomManager, "Clear Legacy Convai Server URL Override");
            _coreServerBaseUrlProp.stringValue = string.Empty;
            EditorUtility.SetDirty(_roomManager);
            serializedObject.ApplyModifiedProperties();
            serializedObject.Update();
        }
    }
}
