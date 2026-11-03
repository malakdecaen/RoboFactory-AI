using System.Collections;
using Newtonsoft.Json.Linq;
using RoboFactory.HUD;
using RoboFactory.Logistics;
using RoboFactory.MetaServer;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace RoboFactory.Launcher
{
    /// <summary>Installs the launcher in every scene that contains a MuseOrchestrator.</summary>
    public static class LauncherBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (Object.FindFirstObjectByType<MuseOrchestrator>() == null) return;
            if (Object.FindFirstObjectByType<LauncherScreen>() != null) return;
            new GameObject("RoboFactory Launcher").AddComponent<LauncherScreen>();
        }
    }

    /// <summary>
    /// Start screen: RECORDED DEMO (default, no key, no network) or LIVE AI (the user's Meta Model API
    /// key, kept in memory only). Drawn once with the HUD design system, then hidden: it is not part of
    /// the HUD. After a scenario ends, a result card offers REPLAY / SCENARIOS (F5 also reopens the launcher).
    /// </summary>
    public sealed class LauncherScreen : MonoBehaviour
    {
        /// <summary>True while the launcher is on screen (HUD shortcuts are ignored meanwhile).</summary>
        public static bool Active { get; private set; }

        private enum Step { Mode, Recorded, LiveKey, LiveChoose }

        private static readonly Color Bg = new Color(0.012f, 0.035f, 0.065f, 1f);

        private Canvas canvas;
        private RectTransform root, pageMode, pageRecorded, pageLiveKey, pageLiveChoose, resultCard;
        private TextMeshProUGUI recordedStatus, keyStatus, chooseStatus, resultTitle, resultDetail;
        private ScrollRect resultScroll;
        private TMP_InputField keyField;
        private Button connectButton;
        private Step step = Step.Mode;
        private ScenarioPlayer player;
        private bool busy, resultShown;

        // ------------------------------------------------------------------ lifecycle

        private void Start()
        {
            RecordedDemoRuntime.Stop();               // a new run never inherits a previous recording / replay
            player = gameObject.AddComponent<ScenarioPlayer>();
            BuildCanvas();
            if (RunMode.Chosen && TryResumeRun()) { Hide(); return; }
            RunMode.Clear();
            RecordedDemoRuntime.Stop();
            Show(Step.Mode);
        }

        private void OnDestroy() { Active = false; }

        /// <summary>After a scene reload: starts the same run again, without showing the launcher.</summary>
        private bool TryResumeRun()
        {
            string error = "";
            if (RunMode.Kind == RunKind.Recorded) return player.BeginReplay(RunMode.ScenarioId, out error);
            if (RunMode.Kind == RunKind.Live)
            {
                if (!MetaServerHost.KeyPresent) return false;
                return !RunMode.Recording || player.BeginRecording(RunMode.ScenarioId, out error);
            }
            return false;
        }

        private void Update()
        {
            if (Active)
            {
                if (HudInput.KeyDown(KeyCode.Escape) && !busy)
                {
                    if (step == Step.Recorded || step == Step.LiveKey || step == Step.LiveChoose) Show(Step.Mode);
                }
                else if (HudInput.KeyDown(KeyCode.Return) && !busy)
                {
                    if (step == Step.Mode) OpenRecorded();
                    else if (step == Step.LiveKey) StartCoroutine(Verify());
                }
                return;
            }
            if (HudInput.KeyDown(KeyCode.F5) && RunMode.Chosen) RunMode.ReturnToLauncher();
            if (!resultShown && player != null && player.State == ScenarioPlayer.PlayerState.Complete) ShowResult();
        }

        // ------------------------------------------------------------------ canvas and pages

        private void BuildCanvas()
        {
            var go = new GameObject("Launcher Canvas", typeof(RectTransform));
            go.layer = 5;
            go.transform.SetParent(transform, false);
            canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 400;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f;
            go.AddComponent<GraphicRaycaster>();
            HudInput.EnsureEventSystem();

            root = HudFactory.Rect("Launcher", go.transform);
            HudFactory.Stretch(root);
            Image back = root.gameObject.AddComponent<Image>();
            back.color = Bg;                          // opaque: hides the warehouse and blocks every click
            back.raycastTarget = true;

            // header
            Image brain = HudFactory.Icon(root, "Brain", HudIcon.Brain, 78f, HudTheme.Cyan);
            HudFactory.Place(brain.rectTransform, new Vector2(.5f, 1f), new Vector2(0f, -86f), new Vector2(78f, 78f));
            TextMeshProUGUI title = HudFactory.Text(root, "Title", "ROBOFACTORY AI", 46f, HudTheme.TextHi, true, TextAlignmentOptions.Center, 3f);
            HudFactory.Place(title.rectTransform, new Vector2(.5f, 1f), new Vector2(0f, -170f), new Vector2(900f, 60f));
            TextMeshProUGUI sub = HudFactory.Text(root, "Subtitle", "AUTONOMOUS WAREHOUSE  ·  POWERED BY META", HudTheme.FontBody, HudTheme.TextLow, false, TextAlignmentOptions.Center, 3f);
            HudFactory.Place(sub.rectTransform, new Vector2(.5f, 1f), new Vector2(0f, -224f), new Vector2(900f, 28f));
            TextMeshProUGUI foot = HudFactory.Text(root, "Footer", "UNITY 6  ·  ROBOFACTORY AI", HudTheme.FontSmall, HudTheme.TextLow, false, TextAlignmentOptions.Center, 3f);
            HudFactory.Place(foot.rectTransform, new Vector2(.5f, 0f), new Vector2(0f, 34f), new Vector2(900f, 24f));

            pageMode = Page("Page Mode");
            pageRecorded = Page("Page Recorded");
            pageLiveKey = Page("Page Live Key");
            pageLiveChoose = Page("Page Live Choose");
            BuildModePage();
            BuildRecordedPage();
            BuildLiveKeyPage();
            BuildLiveChoosePage();
        }

        private RectTransform Page(string name)
        {
            RectTransform p = HudFactory.Rect(name, root);
            HudFactory.Place(p, new Vector2(.5f, 1f), new Vector2(0f, -270f), new Vector2(1000f, 700f));
            return p;
        }

        private void Show(Step s)
        {
            step = s;
            Active = true;
            root.gameObject.SetActive(true);
            pageMode.gameObject.SetActive(s == Step.Mode);
            pageRecorded.gameObject.SetActive(s == Step.Recorded);
            pageLiveKey.gameObject.SetActive(s == Step.LiveKey);
            pageLiveChoose.gameObject.SetActive(s == Step.LiveChoose);
            if (s == Step.Recorded) RefreshRecordedRows();
            if (s == Step.LiveKey) keyField.ActivateInputField();
            if (resultCard != null) resultCard.gameObject.SetActive(false);
        }

        private void Hide()
        {
            Active = false;
            root.gameObject.SetActive(false);
            resultShown = false;
        }

        // ------------------------------------------------------------------ page 1: mode

        private void BuildModePage()
        {
            Card(pageMode, 0f, 168f, HudIcon.Play, "RECORDED DEMO",
                 "Pre-recorded demonstrations of real Meta decisions. The robots, boxes, doors and trucks really run in Unity.",
                 HudIcon.Check, "No API key  ·  No Internet connection", HudTheme.Green, HudTheme.Cyan, "DEFAULT",
                 () => OpenRecorded());
            Card(pageMode, 188f, 168f, HudIcon.Bolt, "LIVE AI",
                 "Real Muse Wonder decisions and voice commands through the Meta Model API.",
                 HudIcon.Key, "Personal API key and connection required", HudTheme.Amber, HudTheme.Blue, null,
                 () => Show(RunMode.LiveKeyVerified && MetaServerHost.KeyPresent ? Step.LiveChoose : Step.LiveKey));
            TextMeshProUGUI hint = HudFactory.Text(pageMode, "Hint", "Click a mode  ·  Enter = RECORDED DEMO", HudTheme.FontSmall, HudTheme.TextLow, false, TextAlignmentOptions.Center, 2f);
            HudFactory.Place(hint.rectTransform, new Vector2(.5f, 1f), new Vector2(0f, -392f), new Vector2(900f, 24f));
        }

        private Button Card(RectTransform parent, float y, float h, HudIcon icon, string title, string desc,
                            HudIcon bulletIcon, string bullet, Color bulletColor, Color accent, string badge,
                            UnityEngine.Events.UnityAction onClick)
        {
            RectTransform card = HudFactory.GlassPanel(parent, title + " Card", HudTheme.GlassLight, HudTheme.A(accent, .65f), 14, true, false);
            HudFactory.Place(card, new Vector2(.5f, 1f), new Vector2(0f, -y), new Vector2(980f, h));
            Button button = card.gameObject.AddComponent<Button>();
            button.targetGraphic = HudFactory.PanelFill(card);
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            ColorBlock cb = button.colors;
            cb.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            cb.pressedColor = new Color(1.6f, 1.6f, 1.6f, 1f);
            button.colors = cb;
            button.onClick.AddListener(() => { HudAudio.Instance?.Play(HudSound.Click); onClick(); });

            Image ic = HudFactory.Icon(card, "Icon", icon, 36f, accent);
            HudFactory.Place(ic.rectTransform, new Vector2(0f, 1f), new Vector2(28f, -18f), new Vector2(36f, 36f));
            TextMeshProUGUI t = HudFactory.Text(card, "Title", title, 32f, HudTheme.TextHi, true, TextAlignmentOptions.Left, 1f);
            HudFactory.Place(t.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(74f, -14f), new Vector2(560f, 42f));
            TextMeshProUGUI d = HudFactory.Text(card, "Description", desc, 22f, HudTheme.TextMid);
            Wrap(d);
            HudFactory.Place(d.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(28f, -62f), new Vector2(920f, 64f));
            Image bi = HudFactory.Icon(card, "Bullet Icon", bulletIcon, 24f, bulletColor);
            HudFactory.Place(bi.rectTransform, new Vector2(0f, 0f), new Vector2(28f, 16f), new Vector2(24f, 24f));
            TextMeshProUGUI b = HudFactory.Text(card, "Bullet", bullet, HudTheme.FontBody, HudTheme.TextHi);
            HudFactory.Place(b.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(64f, 14f), new Vector2(800f, 28f));
            if (badge != null)
            {
                Chip chip = HudFactory.Chip(card, "Badge", badge, HudTheme.Green, 150f, 30f);
                HudFactory.Place(chip.Root, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-26f, -18f), new Vector2(150f, 30f));
            }
            return button;
        }

        // ------------------------------------------------------------------ page 2: recorded scenarios

        private readonly Chip[] recordedChips = new Chip[3];
        private readonly Button[] recordedButtons = new Button[3];

        private void BuildRecordedPage()
        {
            AddHeading(pageRecorded, "RECORDED DEMO  ·  CHOOSE A SCENARIO", HudTheme.Cyan);
            for (int i = 0; i < ScenarioScripts.All.Length; i++)
            {
                ScenarioScript s = ScenarioScripts.All[i];
                string id = s.Id;
                recordedButtons[i] = ScenarioRow(pageRecorded, 56f + i * 124f, s, HudIcon.Play, HudTheme.Cyan, out recordedChips[i],
                                                 () => StartRecorded(id));
            }
            recordedStatus = HudFactory.Text(pageRecorded, "Status", "", HudTheme.FontBody, HudTheme.Amber, false, TextAlignmentOptions.Center);
            Wrap(recordedStatus);
            HudFactory.Place(recordedStatus.rectTransform, new Vector2(.5f, 1f), new Vector2(0f, -436f), new Vector2(940f, 52f));
            BackButton(pageRecorded);
        }

        private void RefreshRecordedRows()
        {
            HudFactory.Set(recordedStatus, "");
            for (int i = 0; i < ScenarioScripts.All.Length; i++)
            {
                bool has = ScenarioPlayer.HasRecording(ScenarioScripts.All[i].Id, out int calls);
                recordedChips[i].Set(has ? calls + " META CALLS" : "NOT RECORDED YET", has ? HudTheme.Green : HudTheme.Amber);
                recordedButtons[i].interactable = has;
            }
        }

        /// <summary>One scenario only: RECORDED DEMO starts it directly when its recording is shipped.</summary>
        private void OpenRecorded()
        {
            if (ScenarioScripts.All.Length == 1 && ScenarioPlayer.HasRecording(ScenarioScripts.All[0].Id, out _))
            {
                StartRecorded(ScenarioScripts.All[0].Id);
                return;
            }
            Show(Step.Recorded);
        }

        private void StartRecorded(string id)
        {
            if (!player.BeginReplay(id, out string error)) { HudFactory.Set(recordedStatus, error); return; }
            RunMode.Choose(RunKind.Recorded, id, false);
            Hide();
        }

        private Button ScenarioRow(RectTransform parent, float y, ScenarioScript s, HudIcon icon, Color accent, out Chip chip,
                                   UnityEngine.Events.UnityAction onClick)
        {
            RectTransform row = HudFactory.GlassPanel(parent, s.Id + " Row", HudTheme.GlassLight, HudTheme.A(accent, .5f), 12, true, false);
            HudFactory.Place(row, new Vector2(.5f, 1f), new Vector2(0f, -y), new Vector2(980f, 108f));
            Button b = row.gameObject.AddComponent<Button>();
            b.targetGraphic = HudFactory.PanelFill(row);
            b.navigation = new Navigation { mode = Navigation.Mode.None };
            ColorBlock cb = b.colors;
            cb.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            cb.pressedColor = new Color(1.6f, 1.6f, 1.6f, 1f);
            cb.disabledColor = new Color(.55f, .55f, .55f, 1f);
            b.colors = cb;
            b.onClick.AddListener(() => { HudAudio.Instance?.Play(HudSound.Click); onClick(); });
            Image ic = HudFactory.Icon(row, "Icon", icon, 34f, accent);
            HudFactory.Place(ic.rectTransform, new Vector2(0f, .5f), new Vector2(28f, 0f), new Vector2(34f, 34f));
            TextMeshProUGUI t = HudFactory.Text(row, "Title", s.Title, 26f, HudTheme.TextHi, true, TextAlignmentOptions.Left, 1.5f);
            HudFactory.Place(t.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(78f, -14f), new Vector2(560f, 34f));
            TextMeshProUGUI d = HudFactory.Text(row, "Summary", s.Summary, 19f, HudTheme.TextMid);
            Wrap(d);
            HudFactory.Place(d.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(78f, -50f), new Vector2(700f, 52f));
            chip = HudFactory.Chip(row, "Status", "", HudTheme.Green, 190f, 30f);
            HudFactory.Place(chip.Root, new Vector2(1f, .5f), new Vector2(1f, .5f), new Vector2(-24f, 0f), new Vector2(190f, 30f));
            return b;
        }

        // ------------------------------------------------------------------ page 3: LIVE AI key

        private void BuildLiveKeyPage()
        {
            AddHeading(pageLiveKey, "LIVE AI  ·  META MODEL API KEY", HudTheme.Blue);
            RectTransform panel = HudFactory.GlassPanel(pageLiveKey, "Key Panel", HudTheme.GlassLight, HudTheme.A(HudTheme.Blue, .6f), 14, true, false);
            HudFactory.Place(panel, new Vector2(.5f, 1f), new Vector2(0f, -56f), new Vector2(980f, 330f));
            Image ic = HudFactory.Icon(panel, "Icon", HudIcon.Key, 30f, HudTheme.Amber);
            HudFactory.Place(ic.rectTransform, new Vector2(0f, 1f), new Vector2(28f, -22f), new Vector2(30f, 30f));
            TextMeshProUGUI l = HudFactory.Text(panel, "Label", "Enter your own Meta Model API key", 24f, HudTheme.TextHi, true, TextAlignmentOptions.Left, 1f);
            HudFactory.Place(l.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(70f, -20f), new Vector2(800f, 34f));
            keyField = BuildKeyField(panel);
            HudFactory.Place((RectTransform)keyField.transform, new Vector2(.5f, 1f), new Vector2(0f, -78f), new Vector2(920f, 56f));
            TextMeshProUGUI note = HudFactory.Text(panel, "Note",
                MetaServerHost.KeyPresent
                    ? "A key is already available on this computer (MODEL_API_KEY). Leave the field empty to use it. Your key stays in memory only: never saved to a file, a log or GitHub."
                    : "Your key stays in memory only: never saved to a file, a log or GitHub. It is checked with real requests to Muse Wonder and Muse Spark.",
                HudTheme.FontBody, HudTheme.TextMid);
            Wrap(note);
            HudFactory.Place(note.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(30f, -148f), new Vector2(920f, 60f));
            connectButton = HudFactory.Button(panel, "Connect", "VERIFY AND CONNECT", HudTheme.Cyan, () => { if (!busy) StartCoroutine(Verify()); }, 300f, 46f, HudTheme.FontBody);
            HudFactory.Place((RectTransform)connectButton.transform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(30f, 24f), new Vector2(300f, 46f));
            keyStatus = HudFactory.Text(panel, "Status", "", HudTheme.FontBody, HudTheme.TextMid);
            Wrap(keyStatus);
            HudFactory.Place(keyStatus.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(350f, 14f), new Vector2(600f, 70f));
            BackButton(pageLiveKey);
        }

        private TMP_InputField BuildKeyField(RectTransform parent)
        {
            RectTransform rt = HudFactory.Rect("Key Field", parent);
            rt.gameObject.SetActive(false);
            Image bg = rt.gameObject.AddComponent<Image>();
            bg.sprite = HudSprites.Rounded(4);
            bg.type = Image.Type.Sliced;
            bg.color = new Color(0f, .03f, .07f, .98f);
            Image stroke = HudFactory.Image(rt, "Stroke", HudSprites.Rounded(4, 1f), HudTheme.Stroke);
            HudFactory.Stretch(stroke.rectTransform);
            RectTransform area = HudFactory.Stretch(HudFactory.Rect("Text Area", rt), 14f, 4f, 12f, 4f);
            area.gameObject.AddComponent<RectMask2D>();
            TextMeshProUGUI placeholder = HudFactory.Text(area, "Placeholder", "Paste your API key here", 22f, HudTheme.TextLow);
            placeholder.fontStyle = FontStyles.Italic;
            placeholder.alignment = TextAlignmentOptions.MidlineLeft;
            HudFactory.Stretch(placeholder.rectTransform);
            TextMeshProUGUI text = HudFactory.Text(area, "Text", "", 22f, HudTheme.TextHi);
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.overflowMode = TextOverflowModes.Overflow;
            HudFactory.Stretch(text.rectTransform);
            var field = rt.gameObject.AddComponent<TMP_InputField>();
            field.textViewport = area;
            field.textComponent = text;
            field.placeholder = placeholder;
            field.fontAsset = HudFonts.Regular;
            field.pointSize = 22f;
            field.caretColor = HudTheme.Cyan;
            field.customCaretColor = true;
            field.selectionColor = HudTheme.A(HudTheme.Cyan, .3f);
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.contentType = TMP_InputField.ContentType.Password;   // the key is never displayed
            field.characterLimit = 400;
            field.targetGraphic = bg;
            field.navigation = new Navigation { mode = Navigation.Mode.None };
            rt.gameObject.SetActive(true);
            return field;
        }

        private IEnumerator Verify()
        {
            busy = true;
            connectButton.interactable = false;
            SetStatus(keyStatus, "Checking the key with real Meta requests…", HudTheme.Cyan);
            string typed = keyField.text.Trim();
            bool typedKey = typed.Length > 0;
            if (typedKey) MetaServerHost.SetApiKey(typed);
            keyField.text = "";                       // the field never keeps the key
            typed = null;

            if (!MetaServerHost.KeyPresent) { Fail("Enter your Meta Model API key."); yield break; }
            if (!MetaServerHost.IsReady) { Fail("The built-in Meta server is not running: " + MetaServerHost.StatusText); yield break; }

            using (var req = new UnityWebRequest(MetaServerHost.Url + "/verify", "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes("{}"));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 110;
                yield return req.SendWebRequest();
                string body = req.downloadHandler != null ? req.downloadHandler.text : "";
                JObject j = null;
                try { j = JObject.Parse(body); } catch (System.Exception) { }
                if (j == null)
                {
                    if (typedKey) MetaServerHost.SetApiKey("");
                    Fail("No answer from the Meta server (" + req.error + ").");
                    yield break;
                }
                bool ok = (bool?)j["ok"] ?? false;
                string lines = "";
                if (j["models"] is JArray models)
                    foreach (JToken m in models)
                        lines += ((bool?)m["ok"] ?? false
                            ? "OK  " + (string)m["model"] + "  ·  " + (int?)m["latency_ms"] + " ms"
                            : "FAILED  " + (string)m["model"] + "  ·  " + (string)m["error"]) + "\n";
                if (!ok)
                {
                    if (typedKey) MetaServerHost.SetApiKey("");   // a refused key is forgotten at once
                    bool refused = (bool?)j["key_refused"] ?? false;
                    Fail((refused ? "Meta refused this key." : "No Meta model could be reached with this key.") + "\n" + lines.TrimEnd());
                    yield break;
                }
                RunMode.LiveKeyVerified = true;
                chooseStatus.text = "Key verified with real requests\n" + lines.TrimEnd();
                chooseStatus.color = HudTheme.Green;
            }
            busy = false;
            connectButton.interactable = true;
            Show(Step.LiveChoose);
        }

        private void Fail(string message)
        {
            SetStatus(keyStatus, message, HudTheme.Red);
            busy = false;
            connectButton.interactable = true;
        }

        // ------------------------------------------------------------------ page 4: LIVE AI choice

        private void BuildLiveChoosePage()
        {
            AddHeading(pageLiveChoose, "LIVE AI  ·  CONNECTED", HudTheme.Green);
            chooseStatus = HudFactory.Text(pageLiveChoose, "Status", "", HudTheme.FontBody, HudTheme.Green);
            Wrap(chooseStatus);
            HudFactory.Place(chooseStatus.rectTransform, new Vector2(.5f, 1f), new Vector2(0f, -50f), new Vector2(940f, 64f));

            RectTransform free = HudFactory.GlassPanel(pageLiveChoose, "Free Play", HudTheme.GlassLight, HudTheme.A(HudTheme.Blue, .65f), 12, true, false);
            HudFactory.Place(free, new Vector2(.5f, 1f), new Vector2(0f, -122f), new Vector2(980f, 96f));
            Button fb = free.gameObject.AddComponent<Button>();
            fb.targetGraphic = HudFactory.PanelFill(free);
            fb.navigation = new Navigation { mode = Navigation.Mode.None };
            fb.onClick.AddListener(() => { HudAudio.Instance?.Play(HudSound.Click); StartLive("", false); });
            Image ic = HudFactory.Icon(free, "Icon", HudIcon.Bolt, 34f, HudTheme.Blue);
            HudFactory.Place(ic.rectTransform, new Vector2(0f, .5f), new Vector2(28f, 0f), new Vector2(34f, 34f));
            TextMeshProUGUI t = HudFactory.Text(free, "Title", "FREE PLAY", 26f, HudTheme.TextHi, true, TextAlignmentOptions.Left, 1.5f);
            HudFactory.Place(t.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(78f, -14f), new Vector2(600f, 34f));
            TextMeshProUGUI d = HudFactory.Text(free, "Summary", "Type free orders, or speak an urgent order with the microphone. Nothing is recorded.", 19f, HudTheme.TextMid);
            Wrap(d);
            HudFactory.Place(d.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(78f, -50f), new Vector2(860f, 40f));

            TextMeshProUGUI rec = HudFactory.Text(pageLiveChoose, "Record Heading",
                "RECORD A SCENARIO  ·  real Meta answers are saved for RECORDED DEMO", HudTheme.FontBody, HudTheme.Red, true, TextAlignmentOptions.Left, 1.5f);
            HudFactory.Place(rec.rectTransform, new Vector2(.5f, 1f), new Vector2(0f, -240f), new Vector2(980f, 28f));
            for (int i = 0; i < ScenarioScripts.All.Length; i++)
            {
                ScenarioScript s = ScenarioScripts.All[i];
                string id = s.Id;
                ScenarioRow(pageLiveChoose, 276f + i * 118f, s, HudIcon.Play, HudTheme.Red, out Chip chip, () => StartLive(id, true));
                chip.Set("● REC", HudTheme.Red);
            }
            BackButton(pageLiveChoose);
        }

        private void StartLive(string scenarioId, bool record)
        {
            if (record && !player.BeginRecording(scenarioId, out string error))
            {
                chooseStatus.text = error; chooseStatus.color = HudTheme.Red;
                return;
            }
            RunMode.Choose(RunKind.Live, scenarioId, record);
            Hide();
        }

        // ------------------------------------------------------------------ result card

        private void ShowResult()
        {
            resultShown = true;
            if (resultCard == null) BuildResultCard();
            resultCard.gameObject.SetActive(true);
            HudFactory.Set(resultTitle, (player.Replay ? "RECORDED DEMO" : "LIVE AI") + "  ·  " + player.Title + "  ·  COMPLETE\n<size=24>" +
                           "<color=#" + HudTheme.Hex(player.Passed ? HudTheme.Green : HudTheme.Amber) + ">" + player.ResultText + "</color></size>");
            HudFactory.Set(resultDetail, player.DetailText);
            // back to the top of the (possibly long) report
            Canvas.ForceUpdateCanvases();
            if (resultScroll != null) resultScroll.verticalNormalizedPosition = 1f;
        }

        private void BuildResultCard()
        {
            resultCard = HudFactory.GlassPanel(canvas.transform, "Scenario Result", HudTheme.GlassStrong, HudTheme.Stroke, 12, true, true);
            HudFactory.Place(resultCard, new Vector2(.5f, 0f), new Vector2(0f, 120f), new Vector2(900f, 300f));
            resultTitle = HudFactory.Text(resultCard, "Title", "", 24f, HudTheme.TextHi, true, TextAlignmentOptions.Left, 1f);
            resultTitle.textWrappingMode = TextWrappingModes.Normal;
            HudFactory.Place(resultTitle.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(26f, -14f), new Vector2(850f, 72f));
            // report in a scrolling view (mouse wheel, drag or the scrollbar): it can be longer than the card
            const float viewW = 830f, viewH = 140f;
            RectTransform view = HudFactory.Rect("Detail View", resultCard);
            HudFactory.Place(view, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(26f, -90f), new Vector2(viewW, viewH));
            view.gameObject.AddComponent<RectMask2D>();
            Image hit = view.gameObject.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, .001f);   // catches the mouse wheel over the whole view
            RectTransform content = HudFactory.Rect("Content", view);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;
            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.padding = new RectOffset(0, 8, 0, 6);
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            resultDetail = HudFactory.Text(content, "Detail", "", HudTheme.FontSmall, HudTheme.TextMid);
            Wrap(resultDetail);
            resultScroll = view.gameObject.AddComponent<ScrollRect>();
            resultScroll.viewport = view;
            resultScroll.content = content;
            resultScroll.horizontal = false;
            resultScroll.vertical = true;
            resultScroll.movementType = ScrollRect.MovementType.Clamped;
            resultScroll.scrollSensitivity = 28f;
            // slider on the right of the report
            RectTransform bar = HudFactory.Rect("Scrollbar", resultCard);
            HudFactory.Place(bar, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(26f + viewW + 8f, -90f), new Vector2(10f, viewH));
            Image track = bar.gameObject.AddComponent<Image>();
            track.sprite = HudSprites.Rounded(4);
            track.type = Image.Type.Sliced;
            track.color = HudTheme.A(HudTheme.Cyan, .12f);
            RectTransform area = HudFactory.Rect("Sliding Area", bar);
            HudFactory.Stretch(area);
            RectTransform handle = HudFactory.Rect("Handle", area);
            HudFactory.Stretch(handle);
            Image knob = handle.gameObject.AddComponent<Image>();
            knob.sprite = HudSprites.Rounded(4);
            knob.type = Image.Type.Sliced;
            knob.color = HudTheme.A(HudTheme.Cyan, .75f);
            var scrollbar = bar.gameObject.AddComponent<Scrollbar>();
            scrollbar.handleRect = handle;
            scrollbar.targetGraphic = knob;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            resultScroll.verticalScrollbar = scrollbar;
            resultScroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            Button again = HudFactory.Button(resultCard, "Replay", "REPLAY", HudTheme.Cyan, () => RunMode.ReloadSame(), 150f, 38f, HudTheme.FontSmall);
            HudFactory.Place((RectTransform)again.transform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(26f, 14f), new Vector2(150f, 38f));
            Button scen = HudFactory.Button(resultCard, "Scenarios", "SCENARIOS  (F5)", HudTheme.Cyan, () => RunMode.ReturnToLauncher(), 210f, 38f, HudTheme.FontSmall);
            HudFactory.Place((RectTransform)scen.transform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(188f, 14f), new Vector2(210f, 38f));
            Button close = HudFactory.Button(resultCard, "Close", "CLOSE", HudTheme.TextLow, () => resultCard.gameObject.SetActive(false), 120f, 38f, HudTheme.FontSmall);
            HudFactory.Place((RectTransform)close.transform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-26f, 14f), new Vector2(120f, 38f));
        }

        // ------------------------------------------------------------------ helpers

        private void AddHeading(RectTransform page, string text, Color color)
        {
            TextMeshProUGUI h = HudFactory.Text(page, "Heading", text, 24f, color, true, TextAlignmentOptions.Left, 3f);
            HudFactory.Place(h.rectTransform, new Vector2(.5f, 1f), new Vector2(0f, -8f), new Vector2(980f, 34f));
        }

        private void BackButton(RectTransform page)
        {
            Button back = HudFactory.Button(page, "Back", "‹  BACK", HudTheme.TextLow, () => { if (!busy) Show(Step.Mode); }, 150f, 40f, HudTheme.FontSmall);
            HudFactory.Place((RectTransform)back.transform, new Vector2(.5f, 1f), new Vector2(-415f, -500f), new Vector2(150f, 40f));
        }

        private static void Wrap(TextMeshProUGUI t)
        {
            t.textWrappingMode = TextWrappingModes.Normal;
            t.overflowMode = TextOverflowModes.Overflow;
            t.alignment = t.alignment == TextAlignmentOptions.Center ? TextAlignmentOptions.Top : TextAlignmentOptions.TopLeft;
        }

        private static void SetStatus(TextMeshProUGUI t, string text, Color c)
        {
            t.text = text;
            t.color = c;
        }
    }
}
