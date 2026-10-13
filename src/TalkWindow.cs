using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FreeWorldTalk
{
    /// <summary>Runtime-only overlay. All gameplay and persistence remain in TalkRuntime.</summary>
    public sealed class TalkWindow : MonoBehaviour
    {
        private TalkRuntime runtime;
        private GameObject overlay;
        private GameObject quickOverlay;
        private GameObject dialogueView;
        private GameObject settingsView;
        private readonly List<GameObject> settingsPages = new List<GameObject>();
        private readonly List<Image> settingsTabs = new List<Image>();
        private int settingsPageIndex;
        private GameObject rolePromptView;
        private GameObject commonRolePromptView;
        private GameObject roleMemoryView;
        private FWHeroBean memoryHero;
        private List<CharacterMemoryRecord> memoryRecords = new List<CharacterMemoryRecord>();
        private CharacterMemoryRecord memoryRecord;
        private RectTransform memoryListContent;
        private TMP_InputField memoryText;
        private TMP_Text memoryDetails;
        private TMP_Text memoryMetadata;
        private Button editRoleMemory;
        private Button dialogueRoleMemory;
        private Button saveRoleMemory;
        private Button deleteRoleMemory;
        private Button promptRoleMemory;
        private readonly Dictionary<string, Image> memoryRows = new Dictionary<string, Image>();
        private TMP_FontAsset font;
        private TMP_Text title;
        private TMP_Text status;
        private TMP_Text transcript;
        private ScrollRect historyScroll;
        private TMP_InputField message;
        private TMP_InputField endpoint;
        private TMP_InputField model;
        private TMP_InputField apiKey;
        private TMP_InputField language;
        private TMP_InputField world;
        private TMP_InputField interval;
        private TMP_InputField rolePrompt;
        private TMP_InputField commonRolePrompt;
        private TMP_Text rolePromptDetails;
        private TMP_Text promptPaths;
        private Toggle automatic;
        private Toggle useCharacterProfile;
        private Button send;
        private Button cancel;
        private Button back;
        private Button clear;
        private Button editRolePrompt;
        private Button saveRolePrompt;
        private Button generateRolePrompt;
        private Button cancelRolePromptGeneration;
        private int rolePromptSession;
        private string previousTranscript;
        private string notice = "";
        private bool clearArmed;
        private BaseHero displayedHero;
        private FWHeroBean promptHero;

        private static readonly Color Panel = new Color(0.075f, 0.10f, 0.13f, 1f);
        private static readonly Color Field = new Color(0.035f, 0.055f, 0.075f, 1f);
        private static readonly Color Ink = new Color(0.87f, 0.92f, 0.95f, 1f);
        private static readonly Color Accent = new Color(0.14f, 0.30f, 0.39f, 1f);

        public bool IsOpen { get { return overlay != null && overlay.activeSelf; } }

        public void Initialize(TalkRuntime owner)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            if (runtime != null) throw new InvalidOperationException("FreeWorldTalk window already initialized.");
            runtime = owner;
            font = FindGameFont();
            Build();
            overlay.SetActive(false);
            RefreshQuickAccess();
        }

        public void Open(BaseHero hero)
        {
            if (hero == null || hero.heroBean == null)
                throw new ArgumentException("Player dialogue requires a live ground hero.", "hero");
            CancelRolePromptGeneration();
            runtime.CancelPlayer();
            CloseRoleMemory();
            runtime.SetSelectedHero(hero);
            displayedHero = hero;
            previousTranscript = null;
            message.SetTextWithoutNotify("");
            notice = "";
            clearArmed = false;
            overlay.SetActive(true);
            dialogueView.SetActive(true);
            settingsView.SetActive(false);
            rolePromptView.SetActive(false);
            commonRolePromptView.SetActive(false);
            promptHero = null;
            Refresh();
            message.ActivateInputField();
        }

        public void OpenSettings()
        {
            CancelRolePromptGeneration();
            CloseRoleMemory();
            if (!IsOpen) overlay.SetActive(true);
            notice = "";
            clearArmed = false;
            dialogueView.SetActive(false);
            settingsView.SetActive(true);
            rolePromptView.SetActive(false);
            commonRolePromptView.SetActive(false);
            promptHero = null;
            PopulateSettings();
            Refresh();
            endpoint.ActivateInputField();
        }

        public void Hide()
        {
            CancelRolePromptGeneration();
            CloseRoleMemory();
            if (runtime != null) runtime.CancelPlayer();
            if (overlay != null) overlay.SetActive(false);
            if (apiKey != null) apiKey.SetTextWithoutNotify("");
            if (message != null) message.SetTextWithoutNotify("");
            if (rolePrompt != null) rolePrompt.SetTextWithoutNotify("");
            if (commonRolePrompt != null) commonRolePrompt.SetTextWithoutNotify("");
            promptHero = null;
            displayedHero = null;
            previousTranscript = null;
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null &&
                EventSystem.current.currentSelectedGameObject.transform.IsChildOf(transform))
                EventSystem.current.SetSelectedGameObject(null);
            notice = "";
            clearArmed = false;
        }

        public void Refresh()
        {
            RefreshQuickAccess();
            if (!IsOpen) return;
            BaseHero hero = runtime.SelectedHero;
            if (displayedHero != hero)
            {
                displayedHero = hero;
                previousTranscript = null;
                message.SetTextWithoutNotify("");
            }
            bool validPromptHero = promptHero != null && runtime.IsCurrentRole(promptHero);
            bool validMemoryHero = memoryHero != null && runtime.IsCurrentRole(memoryHero);
            if (roleMemoryView.activeSelf && !validMemoryHero)
            {
                CloseRoleMemory();
                settingsView.SetActive(true);
                PopulateSettings();
                notice = "当前存档已切换或该角色已离开，请重新打开角色记忆。";
            }
            if (rolePromptView.activeSelf && !validPromptHero)
            {
                CancelRolePromptGeneration();
                rolePromptView.SetActive(false);
                settingsView.SetActive(true);
                promptHero = null;
                rolePrompt.SetTextWithoutNotify("");
                notice = "当前存档已切换或该角色已离开，请重新打开角色提示词。";
            }
            bool validHero = hero != null && hero.heroBean != null;
            title.text = rolePromptView.activeSelf ? "FreeWorld Talk · " + promptHero.Name + " · 角色专属提示词" :
                roleMemoryView.activeSelf ? "FreeWorld Talk · " + memoryHero.Name + " · 角色记忆" :
                commonRolePromptView.activeSelf ? "FreeWorld Talk · 通用角色提示词" : settingsView.activeSelf ? "FreeWorld Talk · 设置" :
                "FreeWorld Talk · " + (validHero ? hero.heroBean.Name : "请先选择地面角色");
            status.text = notice.Length == 0 ? runtime.Status : notice;
            send.interactable = validHero && !runtime.PlayerBusy;
            cancel.interactable = runtime.PlayerBusy;
            back.interactable = validHero;
            clear.interactable = runtime.Store != null && !runtime.Busy;
            editRolePrompt.interactable = validHero;
            editRoleMemory.interactable = validHero;
            dialogueRoleMemory.interactable = validHero;
            promptRoleMemory.interactable = validPromptHero;
            saveRoleMemory.interactable = validMemoryHero;
            deleteRoleMemory.interactable = validMemoryHero && memoryRecord != null;
            saveRolePrompt.interactable = validPromptHero && !runtime.RolePromptBusy;
            generateRolePrompt.interactable = validPromptHero && !runtime.RolePromptBusy;
            cancelRolePromptGeneration.interactable = runtime.RolePromptBusy;
            rolePrompt.readOnly = runtime.RolePromptBusy;
            if (!dialogueView.activeSelf) return;
            var text = new StringBuilder();
            if (validHero && runtime.Store != null)
            {
                foreach (HistoryTurn turn in runtime.Store.HistoryFor(hero.heroBean.uid))
                {
                    text.Append(turn.Speaker).Append("：\n").Append(turn.Text).Append("\n\n");
                }
            }
            if (text.Length == 0)
                text.Append("向角色打个招呼。对话依据角色处境生成，并保留在当前存档的角色记忆中。\n\nEnter 发送。F8 / Esc 关闭。");
            string updated = text.ToString();
            if (updated != previousTranscript)
            {
                bool followBottom = previousTranscript == null || historyScroll.verticalNormalizedPosition < 0.08f;
                transcript.text = updated;
                previousTranscript = updated;
                Canvas.ForceUpdateCanvases();
                if (followBottom) historyScroll.verticalNormalizedPosition = 0f;
            }
        }

        public void RefreshQuickAccess()
        {
            if (quickOverlay == null) return;
            bool visible = isActiveAndEnabled && runtime != null && runtime.HasSelectedTalkHero;
            if (quickOverlay.activeSelf != visible) quickOverlay.SetActive(visible);
        }

        private void Build()
        {
            quickOverlay = Object("FreeWorldTalkQuickAccess", transform);
            quickOverlay.SetActive(false);
            var quickCanvas = quickOverlay.AddComponent<Canvas>();
            quickCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            quickCanvas.sortingOrder = 29999;
            var quickScaler = quickOverlay.AddComponent<CanvasScaler>();
            quickScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            quickScaler.referenceResolution = new Vector2(1280, 800);
            quickScaler.matchWidthOrHeight = 0.5f;
            quickOverlay.AddComponent<GraphicRaycaster>();
            var quick = MakeButton("Talk · F8", quickOverlay.transform, delegate { runtime.ToggleWindow(); });
            Top(quick.GetComponent<RectTransform>(), -1, 16, 82, 34, 126);

            overlay = Object("FreeWorldTalkOverlay", transform);
            var canvas = overlay.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            var scaler = overlay.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 800);
            scaler.matchWidthOrHeight = 0.5f;
            overlay.AddComponent<GraphicRaycaster>();

            var shield = Object("PointerShield", overlay.transform);
            Stretch(shield.GetComponent<RectTransform>(), 0, 0, 0, 0);
            shield.AddComponent<Image>().color = new Color(0, 0, 0, 0.72f);
            var panel = Object("Panel", shield.transform);
            var rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.12f, 0.065f);
            rect.anchorMax = new Vector2(0.88f, 0.935f);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            panel.AddComponent<Image>().color = Panel;
            title = Label("Title", panel.transform, "FreeWorld Talk", 24);
            Top(title.rectTransform, 22, 140, 12, 40);
            var close = MakeButton("关闭", panel.transform, Hide);
            Top(close.GetComponent<RectTransform>(), -1, 16, 14, 38, 94);

            status = Label("Status", panel.transform, "", 16);
            var statusRect = status.rectTransform;
            statusRect.anchorMin = new Vector2(0, 0);
            statusRect.anchorMax = new Vector2(1, 0);
            statusRect.pivot = new Vector2(0.5f, 0);
            statusRect.offsetMin = new Vector2(22, 8);
            statusRect.offsetMax = new Vector2(-22, 46);
            status.color = new Color(0.61f, 0.79f, 0.87f, 1);

            dialogueView = Object("Dialogue", panel.transform);
            Stretch(dialogueView.GetComponent<RectTransform>(), 20, 20, 58, 50);
            settingsView = Object("Settings", panel.transform);
            Stretch(settingsView.GetComponent<RectTransform>(), 20, 20, 58, 50);
            rolePromptView = Object("RolePrompt", panel.transform);
            Stretch(rolePromptView.GetComponent<RectTransform>(), 20, 20, 58, 50);
            commonRolePromptView = Object("CommonRolePrompt", panel.transform);
            Stretch(commonRolePromptView.GetComponent<RectTransform>(), 20, 20, 58, 50);
            roleMemoryView = Object("RoleMemory", panel.transform);
            Stretch(roleMemoryView.GetComponent<RectTransform>(), 20, 20, 58, 50);
            BuildDialogue();
            BuildSettings();
            BuildRolePrompt();
            BuildCommonRolePrompt();
            BuildRoleMemory();
        }

        private void BuildDialogue()
        {
            var scrollObject = Object("History", dialogueView.transform);
            Stretch(scrollObject.GetComponent<RectTransform>(), 0, 0, 0, 160);
            scrollObject.AddComponent<Image>().color = Field;
            var viewport = Object("Viewport", scrollObject.transform);
            Stretch(viewport.GetComponent<RectTransform>(), 12, 12, 12, 12);
            viewport.AddComponent<RectMask2D>();
            transcript = Label("Transcript", viewport.transform, "", 20);
            var content = transcript.rectTransform;
            content.anchorMin = new Vector2(0, 1);
            content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;
            transcript.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            historyScroll = scrollObject.AddComponent<ScrollRect>();
            historyScroll.viewport = viewport.GetComponent<RectTransform>();
            historyScroll.content = content;
            historyScroll.horizontal = false;
            historyScroll.movementType = ScrollRect.MovementType.Clamped;
            historyScroll.scrollSensitivity = 24f;

            message = MakeInput("Message", dialogueView.transform, "输入你想说的话…", true);
            Bottom(message.GetComponent<RectTransform>(), 0, 0, 48, 100);
            message.characterLimit = 2000;
            message.lineType = TMP_InputField.LineType.MultiLineSubmit;
            message.onSubmit.AddListener(delegate { Send(); });
            send = MakeButton("发送", dialogueView.transform, Send);
            Bottom(send.GetComponent<RectTransform>(), 0, -1, 0, 38, 112);
            cancel = MakeButton("取消请求", dialogueView.transform, delegate { runtime.CancelPlayer(); Refresh(); });
            Bottom(cancel.GetComponent<RectTransform>(), 124, -1, 0, 38, 112);
            dialogueRoleMemory = MakeButton("角色记忆", dialogueView.transform, OpenSelectedRoleMemory);
            Bottom(dialogueRoleMemory.GetComponent<RectTransform>(), 248, -1, 0, 38, 112);
            var settings = MakeButton("设置 / 记忆", dialogueView.transform, OpenSettings);
            Bottom(settings.GetComponent<RectTransform>(), -1, 0, 0, 38, 152);
        }

        private void BuildSettings()
        {
            string[] captions = { "接口", "对话", "提示词", "记忆" };
            for (int i = 0; i < captions.Length; i++)
            {
                int page = i;
                var tab = MakeButton(captions[i], settingsView.transform, delegate { SelectSettingsPage(page); });
                var tabRect = tab.GetComponent<RectTransform>();
                tabRect.anchorMin = new Vector2(i / 4f, 1);
                tabRect.anchorMax = new Vector2((i + 1) / 4f, 1);
                tabRect.offsetMin = new Vector2(i == 0 ? 0 : 6, -38);
                tabRect.offsetMax = new Vector2(i == 3 ? 0 : -6, 0);
                settingsTabs.Add(tab.GetComponent<Image>());
            }
            RectTransform apiContent = MakeSettingsPage("Interface", 312);
            RectTransform dialogueContent = MakeSettingsPage("Conversation", 126);
            RectTransform promptContent = MakeSettingsPage("Prompts", 408);
            RectTransform memoryContent = MakeSettingsPage("Memory", 346);

            endpoint = SettingField(apiContent, "接口地址", "https://api.deepseek.com", 0);
            model = SettingField(apiContent, "模型", "deepseek-chat", 76);
            apiKey = SettingField(apiContent, "API Key（本机加密保存）", "输入 DeepSeek API Key", 152);
            apiKey.contentType = TMP_InputField.ContentType.Password;
            apiKey.asteriskChar = '*';
            language = SettingField(apiContent, "回复语言", "中文", 228);
            interval = SettingField(dialogueContent, "自动闲聊最小间隔（秒，10–3600）", "60", 0);
            interval.contentType = TMP_InputField.ContentType.IntegerNumber;

            automatic = SettingToggle(dialogueContent, "AutoTalk", "启用地面角色自动闲聊", 84);
            useCharacterProfile = SettingToggle(promptContent, "UseCharacterProfile", "附加游戏角色背景资料", 0);
            var profileHelp = Label("CharacterProfileHelp", promptContent,
                "关闭后，个人扮演设定仅使用角色 JSON 提示词；当前处境与记忆仍保留。", 16);
            Top(profileHelp.rectTransform, 0, 0, 42, 40);
            profileHelp.color = new Color(0.61f, 0.79f, 0.87f, 1);

            var worldLabel = Label("WorldLabel", promptContent, "世界补充设定（作为背景，不改变游戏规则）", 18);
            Top(worldLabel.rectTransform, 0, 0, 92, 27);
            world = MakeInput("WorldDescription", promptContent, "补充世界背景…", true);
            Top(world.GetComponent<RectTransform>(), 0, 0, 124, 100);
            world.characterLimit = 4000;
            var editCommonRolePrompt = MakeButton("通用角色提示词", promptContent, OpenCommonRolePrompt);
            Top(editCommonRolePrompt.GetComponent<RectTransform>(), 0, -1, 240, 38, 224);
            editRolePrompt = MakeButton("角色专属提示词", promptContent, OpenRolePrompt);
            Top(editRolePrompt.GetComponent<RectTransform>(), 236, -1, 240, 38, 224);
            var reloadPrompts = MakeButton("重载提示词 JSON", promptContent, ReloadPrompts);
            Top(reloadPrompts.GetComponent<RectTransform>(), 0, -1, 292, 38, 190);
            var promptHelp = Label("PromptHelp", promptContent, "通用角色提示词发送给所有角色；角色专属提示词只发送给本人。两者均保存为可修改的 JSON。", 16);
            Top(promptHelp.rectTransform, 0, 0, 342, 58);
            promptHelp.color = new Color(0.61f, 0.79f, 0.87f, 1);
            editRoleMemory = MakeButton("查看 / 编辑角色记忆", memoryContent, OpenSelectedRoleMemory);
            Top(editRoleMemory.GetComponent<RectTransform>(), 0, -1, 0, 38, 224);
            clear = MakeButton("清空当前存档记忆", memoryContent, ClearMemory);
            Top(clear.GetComponent<RectTransform>(), 0, -1, 52, 38, 224);
            var data = Label("DataDirectory", memoryContent,
                "记忆与缓存保存在本机：\n" + runtime.DataPath, 16);
            Top(data.rectTransform, 0, 0, 106, 118);
            data.color = new Color(0.61f, 0.79f, 0.87f, 1);
            var memoryHelp = Label("MemoryHelp", memoryContent,
                "个人记忆可逐条编辑、删除或新增，修改会用于后续对话。请保存游戏，以便将修改写入当前存档。\n清空按钮会清除当前存档全部角色的对话历史与记忆。", 16);
            Top(memoryHelp.rectTransform, 0, 0, 236, 102);
            memoryHelp.color = new Color(0.61f, 0.79f, 0.87f, 1);

            var save = MakeButton("保存设置", settingsView.transform, SaveSettings);
            Bottom(save.GetComponent<RectTransform>(), 0, -1, 0, 38, 120);
            back = MakeButton("返回对话", settingsView.transform, delegate
            {
                apiKey.SetTextWithoutNotify("");
                notice = "";
                dialogueView.SetActive(true);
                settingsView.SetActive(false);
                rolePromptView.SetActive(false);
                commonRolePromptView.SetActive(false);
                Refresh();
            });
            Bottom(back.GetComponent<RectTransform>(), -1, 0, 0, 38, 120);
            SelectSettingsPage(settingsPageIndex);
        }

        private RectTransform MakeSettingsPage(string name, float contentHeight)
        {
            var page = Object(name + "Settings", settingsView.transform);
            Stretch(page.GetComponent<RectTransform>(), 0, 0, 52, 52);
            settingsPages.Add(page);
            var viewport = Object("Viewport", page.transform).GetComponent<RectTransform>();
            Stretch(viewport, 0, 0, 0, 0);
            viewport.gameObject.AddComponent<RectMask2D>();
            var content = Object("Content", viewport).GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0, 1);
            content.anchorMax = Vector2.one;
            content.pivot = new Vector2(0.5f, 1);
            content.sizeDelta = new Vector2(0, contentHeight);
            content.anchoredPosition = Vector2.zero;
            var scroll = page.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24;
            return content;
        }

        private void SelectSettingsPage(int index)
        {
            if (index < 0 || index >= settingsPages.Count) throw new ArgumentOutOfRangeException("index");
            settingsPageIndex = index;
            clearArmed = false;
            for (int i = 0; i < settingsPages.Count; i++)
            {
                settingsPages[i].SetActive(i == index);
                settingsTabs[i].color = i == index ? new Color(0.20f, 0.48f, 0.58f, 1) : Accent;
            }
        }

        private void BuildRolePrompt()
        {
            rolePromptDetails = Label("RoleDetails", rolePromptView.transform, "", 18);
            Top(rolePromptDetails.rectTransform, 0, 0, 0, 68);
            rolePrompt = MakeInput("RolePromptText", rolePromptView.transform, "填写该角色的说话方式、性格和个人设定…", true);
            Stretch(rolePrompt.GetComponent<RectTransform>(), 0, 0, 76, 212);
            rolePrompt.characterLimit = PromptStore.MaxRoleCharacters;
            promptPaths = Label("PromptPaths", rolePromptView.transform, "", 15);
            Bottom(promptPaths.rectTransform, 0, 0, 106, 96);
            promptPaths.color = new Color(0.61f, 0.79f, 0.87f, 1);
            generateRolePrompt = MakeButton("根据角色资料生成", rolePromptView.transform, GenerateRolePrompt);
            Bottom(generateRolePrompt.GetComponent<RectTransform>(), 0, -1, 54, 38, 224);
            cancelRolePromptGeneration = MakeButton("取消生成", rolePromptView.transform, delegate
            {
                CancelRolePromptGeneration();
                notice = "已取消生成，原草稿保留。";
                Refresh();
            });
            Bottom(cancelRolePromptGeneration.GetComponent<RectTransform>(), 236, -1, 54, 38, 130);
            promptRoleMemory = MakeButton("角色记忆", rolePromptView.transform, delegate { runtime.OpenRoleMemory(promptHero); });
            Bottom(promptRoleMemory.GetComponent<RectTransform>(), -1, 0, 54, 38, 120);
            saveRolePrompt = MakeButton("保存角色提示词", rolePromptView.transform, SaveRolePrompt);
            Bottom(saveRolePrompt.GetComponent<RectTransform>(), 0, -1, 0, 38, 170);
            var reload = MakeButton("重载 JSON", rolePromptView.transform, ReloadPrompts);
            Bottom(reload.GetComponent<RectTransform>(), 182, -1, 0, 38, 130);
            var returnToSettings = MakeButton("返回设置", rolePromptView.transform, OpenSettings);
            Bottom(returnToSettings.GetComponent<RectTransform>(), -1, 0, 0, 38, 120);
            rolePromptView.SetActive(false);
        }

        private void BuildCommonRolePrompt()
        {
            var details = Label("CommonRoleDetails", commonRolePromptView.transform,
                "此提示词发送给所有角色，用于共同的说话规则与角色扮演要求。\n最多 " +
                PromptStore.MaxCommonRoleCharacters + " 字符；角色个人设定请编辑角色专属提示词。", 18);
            Top(details.rectTransform, 0, 0, 0, 68);
            commonRolePrompt = MakeInput("CommonRolePromptText", commonRolePromptView.transform, "填写适用于所有角色的角色扮演规则…", true);
            Stretch(commonRolePrompt.GetComponent<RectTransform>(), 0, 0, 76, 160);
            commonRolePrompt.characterLimit = PromptStore.MaxCommonRoleCharacters;
            var path = Label("CommonPromptPath", commonRolePromptView.transform,
                "通用角色提示词 JSON：" + runtime.GlobalPromptPath + "\n字段：CommonRolePrompt", 15);
            Bottom(path.rectTransform, 0, 0, 54, 96);
            path.color = new Color(0.61f, 0.79f, 0.87f, 1);
            var save = MakeButton("保存通用提示词", commonRolePromptView.transform, SaveCommonRolePrompt);
            Bottom(save.GetComponent<RectTransform>(), 0, -1, 0, 38, 170);
            var reload = MakeButton("重载 JSON", commonRolePromptView.transform, ReloadPrompts);
            Bottom(reload.GetComponent<RectTransform>(), 182, -1, 0, 38, 130);
            var returnToSettings = MakeButton("返回设置", commonRolePromptView.transform, OpenSettings);
            Bottom(returnToSettings.GetComponent<RectTransform>(), -1, 0, 0, 38, 120);
            commonRolePromptView.SetActive(false);
        }

        private void BuildRoleMemory()
        {
            memoryDetails = Label("MemoryDetails", roleMemoryView.transform, "", 17);
            Top(memoryDetails.rectTransform, 0, 0, 0, 82);
            var body = Object("MemoryBody", roleMemoryView.transform).GetComponent<RectTransform>();
            Stretch(body, 0, 0, 90, 104);
            var list = Object("MemoryList", body);
            var listRect = list.GetComponent<RectTransform>();
            listRect.anchorMin = Vector2.zero;
            listRect.anchorMax = new Vector2(0.40f, 1);
            listRect.offsetMin = Vector2.zero;
            listRect.offsetMax = new Vector2(-8, 0);
            list.AddComponent<Image>().color = Field;
            var viewport = Object("Viewport", list.transform).GetComponent<RectTransform>();
            Stretch(viewport, 8, 22, 8, 8);
            viewport.gameObject.AddComponent<RectMask2D>();
            memoryListContent = Object("Records", viewport).GetComponent<RectTransform>();
            memoryListContent.anchorMin = new Vector2(0, 1);
            memoryListContent.anchorMax = Vector2.one;
            memoryListContent.pivot = new Vector2(0.5f, 1);
            memoryListContent.anchoredPosition = Vector2.zero;
            memoryListContent.sizeDelta = Vector2.zero;
            var scroll = list.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = memoryListContent;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30;
            var bar = Object("Scrollbar", list.transform);
            var barRect = bar.GetComponent<RectTransform>();
            barRect.anchorMin = new Vector2(1, 0);
            barRect.anchorMax = Vector2.one;
            barRect.offsetMin = new Vector2(-16, 8);
            barRect.offsetMax = new Vector2(-6, -8);
            bar.AddComponent<Image>().color = Panel;
            var thumb = Object("Handle", bar.transform);
            Stretch(thumb.GetComponent<RectTransform>(), 0, 0, 0, 0);
            var thumbImage = thumb.AddComponent<Image>();
            thumbImage.color = Accent;
            var scrollbar = bar.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scrollbar.handleRect = thumb.GetComponent<RectTransform>();
            scrollbar.targetGraphic = thumbImage;
            scrollbar.navigation = new Navigation { mode = Navigation.Mode.None };
            scroll.verticalScrollbar = scrollbar;
            var editor = Object("MemoryEditor", body).GetComponent<RectTransform>();
            editor.anchorMin = new Vector2(0.40f, 0);
            editor.anchorMax = Vector2.one;
            editor.offsetMin = new Vector2(8, 0);
            editor.offsetMax = Vector2.zero;
            memoryMetadata = Label("Metadata", editor, "", 16);
            Top(memoryMetadata.rectTransform, 0, 0, 0, 76);
            memoryMetadata.color = new Color(0.61f, 0.79f, 0.87f, 1);
            memoryText = MakeInput("MemoryText", editor, "选择左侧记忆查看与编辑，或点击新增…", true);
            Stretch(memoryText.GetComponent<RectTransform>(), 0, 0, 84, 0);
            memoryText.characterLimit = MemoryStore.MaxRecordCharacters;
            var discard = MakeButton("放弃未保存草稿", roleMemoryView.transform, DiscardMemoryDraft);
            Bottom(discard.GetComponent<RectTransform>(), 0, -1, 52, 38, 170);
            var limit = Label("MemoryLimit", roleMemoryView.transform, "每条最多 " + MemoryStore.MaxRecordCharacters + " 字符；来源和时间保留。", 16);
            Bottom(limit.rectTransform, 182, 0, 52, 38);
            saveRoleMemory = MakeButton("保存本条", roleMemoryView.transform, SaveMemoryRecord);
            Bottom(saveRoleMemory.GetComponent<RectTransform>(), 0, -1, 0, 38, 102);
            deleteRoleMemory = MakeButton("删除本条", roleMemoryView.transform, DeleteMemoryRecord);
            Bottom(deleteRoleMemory.GetComponent<RectTransform>(), 114, -1, 0, 38, 102);
            var add = MakeButton("新增记忆", roleMemoryView.transform, NewMemoryRecord);
            Bottom(add.GetComponent<RectTransform>(), 228, -1, 0, 38, 102);
            var reload = MakeButton("重载列表", roleMemoryView.transform, delegate { ReloadMemoryRecords(true); });
            Bottom(reload.GetComponent<RectTransform>(), 342, -1, 0, 38, 102);
            var backToSettings = MakeButton("返回设置", roleMemoryView.transform, OpenSettings);
            Bottom(backToSettings.GetComponent<RectTransform>(), -1, 0, 0, 38, 120);
            roleMemoryView.SetActive(false);
        }

        private void OpenSelectedRoleMemory()
        {
            runtime.OpenRoleMemory(runtime.SelectedHero == null ? null : runtime.SelectedHero.heroBean);
        }

        public void OpenRoleMemory(FWHeroBean hero)
        {
            if (hero == null) return;
            List<CharacterMemoryRecord> records;
            try { records = runtime.GetRoleMemories(hero); }
            catch (Exception error)
            {
                if (!IsOpen) OpenSettings();
                notice = "读取角色记忆失败：" + error.Message;
                Refresh();
                return;
            }
            CancelRolePromptGeneration();
            runtime.CancelPlayer();
            memoryHero = hero;
            memoryRecords = records;
            memoryRecord = null;
            memoryText.SetTextWithoutNotify("");
            memoryDetails.text = hero.Name + " · UID " + hero.uid +
                "\n查看该角色的对话与事件记忆。修改会用于后续对话；保存游戏后写入存档。\n编辑后点击保存本条；未保存草稿不会写入。";
            apiKey.SetTextWithoutNotify("");
            overlay.SetActive(true);
            dialogueView.SetActive(false);
            settingsView.SetActive(false);
            rolePromptView.SetActive(false);
            commonRolePromptView.SetActive(false);
            roleMemoryView.SetActive(true);
            notice = "";
            RebuildMemoryList();
            SetMemoryRecord(records.Count > 0 ? records[0] : null);
            Refresh();
        }

        private void CloseRoleMemory()
        {
            if (roleMemoryView != null) roleMemoryView.SetActive(false);
            memoryHero = null;
            memoryRecord = null;
            memoryRecords.Clear();
            if (memoryText != null) memoryText.SetTextWithoutNotify("");
        }

        private bool HasMemoryDraft()
        {
            return memoryText.text != (memoryRecord == null ? "" : memoryRecord.Text);
        }

        private void RebuildMemoryList()
        {
            for (int i = memoryListContent.childCount - 1; i >= 0; i--)
            {
                GameObject row = memoryListContent.GetChild(i).gameObject;
                row.SetActive(false);
                Destroy(row);
            }
            memoryRows.Clear();
            memoryListContent.sizeDelta = new Vector2(0, Math.Max(96, memoryRecords.Count * 110));
            if (memoryRecords.Count == 0)
            {
                var empty = Label("Empty", memoryListContent, "该角色暂无记忆。\n可以新增手工记忆。", 18);
                Top(empty.rectTransform, 8, 8, 8, 90);
                return;
            }
            for (int i = 0; i < memoryRecords.Count; i++)
            {
                CharacterMemoryRecord record = memoryRecords[i];
                string preview = (record.Text ?? "").Replace('\r', ' ').Replace('\n', ' ');
                if (preview.Length > 65) preview = preview.Substring(0, 65) + "…";
                var button = MakeButton(MemoryCaption(record) + "\n" + preview, memoryListContent,
                    delegate { SelectMemoryRecord(record); });
                Top(button.GetComponent<RectTransform>(), 0, 0, i * 110, 102);
                TMP_Text label = button.GetComponentInChildren<TMP_Text>();
                label.fontSize = 16;
                label.alignment = TextAlignmentOptions.TopLeft;
                label.margin = new Vector4(8, 5, 8, 5);
                label.overflowMode = TextOverflowModes.Ellipsis;
                memoryRows.Add(record.RecordId, button.GetComponent<Image>());
            }
            UpdateMemorySelection();
        }

        private static string MemoryCaption(CharacterMemoryRecord record)
        {
            return (record.Kind == "history" ? "对话" : "事件") + " · " + record.Speaker +
                (record.EditedByPlayer ? " · 玩家编辑" : "") + "\n游戏累计分钟 " + record.SimulationMM;
        }

        private void SelectMemoryRecord(CharacterMemoryRecord record)
        {
            if (memoryRecord != null && memoryRecord.RecordId == record.RecordId) return;
            if (HasMemoryDraft())
            {
                notice = "当前有未保存草稿，请先保存本条或点击“放弃未保存草稿”，再选择其他记忆。";
                Refresh();
                return;
            }
            SetMemoryRecord(record);
            notice = "";
            Refresh();
        }

        private void SetMemoryRecord(CharacterMemoryRecord record)
        {
            memoryRecord = record;
            memoryText.SetTextWithoutNotify(record == null ? "" : record.Text);
            memoryMetadata.text = record == null ? "新增手工记忆\n填写后点击“保存本条”。" :
                MemoryCaption(record) + "\n仅修改此条内容，其他角色与记忆不受影响。";
            UpdateMemorySelection();
        }

        private void UpdateMemorySelection()
        {
            foreach (var pair in memoryRows)
                pair.Value.color = memoryRecord != null && pair.Key == memoryRecord.RecordId ?
                    new Color(0.20f, 0.48f, 0.58f, 1) : Accent;
        }

        private void NewMemoryRecord()
        {
            if (HasMemoryDraft())
            {
                notice = "当前有未保存草稿，请先保存本条或点击“放弃未保存草稿”。";
                Refresh();
                return;
            }
            SetMemoryRecord(null);
            notice = "填写一条新的个人记忆，点击保存本条后生效。";
            Refresh();
            memoryText.ActivateInputField();
        }

        private void ReloadMemoryRecords(bool preserveDraft)
        {
            if (memoryHero == null || !runtime.IsCurrentRole(memoryHero)) { Refresh(); return; }
            bool dirty = preserveDraft && HasMemoryDraft();
            string recordId = memoryRecord == null ? null : memoryRecord.RecordId;
            try
            {
                memoryRecords = runtime.GetRoleMemories(memoryHero);
                RebuildMemoryList();
                if (!dirty)
                    SetMemoryRecord(recordId == null ? null : memoryRecords.Find(item => item.RecordId == recordId));
                notice = dirty ? "列表已重载，未保存草稿已保留；保存时会检查原记录是否被修改。" : "角色记忆列表已更新。";
            }
            catch (Exception error) { notice = "重载角色记忆失败：" + error.Message + "；原草稿已保留。"; }
            Refresh();
        }

        private void DiscardMemoryDraft()
        {
            ReloadMemoryRecords(false);
        }

        private void SaveMemoryRecord()
        {
            if (memoryHero == null || !runtime.IsCurrentRole(memoryHero)) { Refresh(); return; }
            notice = "";
            bool saved = memoryRecord == null ? runtime.AddRoleMemory(memoryHero, memoryText.text) :
                runtime.EditRoleMemory(memoryHero, memoryRecord, memoryText.text);
            if (saved)
            {
                string recordId = memoryRecord == null ? null : memoryRecord.RecordId;
                memoryText.SetTextWithoutNotify(memoryRecord == null ? "" : memoryRecord.Text);
                ReloadMemoryRecords(false);
                if (recordId == null) SetMemoryRecord(null);
                notice = "本条记忆已保存，后续对话使用新内容。请保存游戏以写入存档。";
            }
            Refresh();
        }

        private void DeleteMemoryRecord()
        {
            if (memoryHero == null || memoryRecord == null || !runtime.IsCurrentRole(memoryHero)) { Refresh(); return; }
            if (HasMemoryDraft())
            {
                notice = "当前有未保存草稿，请先保存或放弃草稿，再删除本条。";
                Refresh();
                return;
            }
            notice = "";
            if (runtime.DeleteRoleMemory(memoryHero, memoryRecord))
            {
                SetMemoryRecord(null);
                ReloadMemoryRecords(false);
                notice = "本条记忆已删除。请保存游戏以写入存档。";
            }
            Refresh();
        }

        private void OpenCommonRolePrompt()
        {
            CancelRolePromptGeneration();
            runtime.CancelPlayer();
            string prompt;
            try { prompt = runtime.GetCommonRolePrompt(); }
            catch (Exception error)
            {
                if (!IsOpen) OpenSettings();
                apiKey.SetTextWithoutNotify("");
                notice = "读取通用角色提示词失败：" + error.Message;
                Refresh();
                return;
            }
            apiKey.SetTextWithoutNotify("");
            CloseRoleMemory();
            overlay.SetActive(true);
            commonRolePrompt.SetTextWithoutNotify(prompt ?? "");
            promptHero = null;
            notice = "";
            dialogueView.SetActive(false);
            settingsView.SetActive(false);
            rolePromptView.SetActive(false);
            commonRolePromptView.SetActive(true);
            Refresh();
            commonRolePrompt.ActivateInputField();
        }

        private void SaveCommonRolePrompt()
        {
            notice = "";
            if (runtime.SaveCommonRolePrompt(commonRolePrompt.text))
                notice = "通用角色提示词已保存。所有角色的后续对话使用新内容。";
            Refresh();
        }

        private void OpenRolePrompt()
        {
            OpenRolePrompt(runtime.SelectedHero == null ? null : runtime.SelectedHero.heroBean);
        }

        public void OpenRolePrompt(BaseHero hero)
        {
            OpenRolePrompt(hero == null ? null : hero.heroBean);
        }

        public void OpenRolePrompt(FWHeroBean hero)
        {
            if (hero == null) return;
            CancelRolePromptGeneration();
            runtime.CancelPlayer();
            string prompt;
            try { prompt = runtime.GetRolePrompt(hero); }
            catch (Exception error)
            {
                if (!IsOpen) OpenSettings();
                apiKey.SetTextWithoutNotify("");
                notice = "读取角色提示词失败：" + error.Message;
                Refresh();
                return;
            }
            promptHero = hero;
            CloseRoleMemory();
            apiKey.SetTextWithoutNotify("");
            overlay.SetActive(true);
            rolePrompt.SetTextWithoutNotify(prompt ?? "");
            rolePromptDetails.text = hero.Name + " · UID " + hero.uid +
                "\n专属设定最多 " + PromptStore.MaxRoleCharacters + " 字符，保存为角色 JSON。\n生成草稿会使用完整角色资料，不受附加游戏背景开关限制。";
            promptPaths.text = "角色 JSON 目录：" + runtime.PromptDirectory;
            notice = "";
            dialogueView.SetActive(false);
            settingsView.SetActive(false);
            rolePromptView.SetActive(true);
            commonRolePromptView.SetActive(false);
            Refresh();
            rolePrompt.ActivateInputField();
        }

        private void SaveRolePrompt()
        {
            if (promptHero == null || !runtime.IsCurrentRole(promptHero)) { Refresh(); return; }
            notice = "";
            if (runtime.SaveRolePrompt(promptHero, rolePrompt.text))
                notice = "该角色提示词已保存。后续对话使用新内容。";
            Refresh();
        }

        private void GenerateRolePrompt()
        {
            if (promptHero == null || !runtime.IsCurrentRole(promptHero) || runtime.RolePromptBusy) { Refresh(); return; }
            FWHeroBean target = promptHero;
            int session = rolePromptSession;
            notice = "";
            try
            {
                runtime.GenerateRolePrompt(target, delegate(RolePromptResponse response)
                {
                    if (this == null || !IsOpen || !rolePromptView.activeSelf || session != rolePromptSession ||
                        !ReferenceEquals(promptHero, target) || !runtime.IsCurrentRole(target)) return;
                    if (response == null || string.IsNullOrWhiteSpace(response.Prompt))
                        notice = "生成内容为空，原草稿已保留。";
                    else if (response.Prompt.Length > PromptStore.MaxRoleCharacters)
                        notice = "生成内容超过 " + PromptStore.MaxRoleCharacters + " 字符，原草稿已保留。";
                    else
                    {
                        rolePrompt.SetTextWithoutNotify(response.Prompt);
                        notice = "已根据资料生成草稿，确认后点击保存。token=" +
                            ((long)response.PromptTokens + response.CompletionTokens);
                    }
                    Refresh();
                });
            }
            catch (Exception error)
            {
                notice = "生成角色提示词失败：" + error.Message;
            }
            Refresh();
        }

        private void CancelRolePromptGeneration()
        {
            rolePromptSession++;
            if (runtime != null) runtime.CancelRolePrompt();
        }

        private void ReloadPrompts()
        {
            CancelRolePromptGeneration();
            notice = "";
            runtime.ReloadPrompts();
            // Preserve the editor draft. Reopening this page explicitly loads the saved JSON.
            Refresh();
        }

        private void Send()
        {
            if (!IsOpen || runtime.PlayerBusy || runtime.SelectedHero == null || string.IsNullOrWhiteSpace(message.text)) return;
            string text = message.text.Trim();
            notice = "";
            runtime.SendPlayer(text);
            if (runtime.PlayerBusy) message.SetTextWithoutNotify("");
            message.ActivateInputField();
            Refresh();
        }

        private void PopulateSettings()
        {
            TalkSettings settings = runtime.Settings;
            endpoint.SetTextWithoutNotify(settings.Endpoint);
            model.SetTextWithoutNotify(settings.Model);
            apiKey.SetTextWithoutNotify(settings.ApiKey ?? "");
            language.SetTextWithoutNotify(settings.Language);
            world.SetTextWithoutNotify(settings.WorldDescription);
            interval.SetTextWithoutNotify(settings.AutoIntervalSeconds.ToString());
            automatic.SetIsOnWithoutNotify(settings.AutoEnabled);
            useCharacterProfile.SetIsOnWithoutNotify(settings.UseCharacterProfile);
        }

        private void SaveSettings()
        {
            Uri parsed;
            int seconds;
            if (!Uri.TryCreate(endpoint.text.Trim(), UriKind.Absolute, out parsed) || parsed.Scheme != Uri.UriSchemeHttps)
            { notice = "接口地址必须是完整的 HTTPS 地址。"; Refresh(); return; }
            if (string.IsNullOrWhiteSpace(model.text) || string.IsNullOrWhiteSpace(language.text))
            { notice = "请填写模型和回复语言。"; Refresh(); return; }
            if (!int.TryParse(interval.text, out seconds) || seconds < 10 || seconds > 3600)
            { notice = "自动闲聊间隔应为 10–3600 秒的整数。"; Refresh(); return; }
            TalkSettings old = runtime.Settings;
            var updated = new TalkSettings
            {
                Endpoint = endpoint.text.Trim().TrimEnd('/'), Model = model.text.Trim(), ApiKey = apiKey.text.Trim(),
                Language = language.text.Trim(), WorldDescription = world.text.Trim(), AutoEnabled = automatic.isOn,
                UseCharacterProfile = useCharacterProfile.isOn,
                AutoIntervalSeconds = seconds, RequestTimeoutSeconds = old.RequestTimeoutSeconds,
                MaxOutputTokens = old.MaxOutputTokens, RecentTurns = old.RecentTurns,
                MaxMemoriesPerActor = old.MaxMemoriesPerActor, MaxCacheEntries = old.MaxCacheEntries,
                CacheTtlMinutes = old.CacheTtlMinutes
            };
            try { SettingsStore.Validate(updated); }
            catch (ArgumentException error) { notice = error.Message; Refresh(); return; }
            runtime.ApplySettings(updated);
            notice = "";
            Refresh();
        }

        private void ClearMemory()
        {
            if (!clearArmed)
            { clearArmed = true; notice = "再次点击“清空当前存档记忆”，确认清除当前存档全部角色的对话历史与记忆。"; Refresh(); return; }
            runtime.ClearMemory();
            clearArmed = false;
            notice = "";
            previousTranscript = null;
            Refresh();
        }

        private TMP_InputField SettingField(Transform parent, string caption, string placeholder, float y)
        {
            var label = Label(caption + "Label", parent, caption, 18);
            Top(label.rectTransform, 0, 0, y, 26);
            var input = MakeInput(caption, parent, placeholder, false);
            Top(input.GetComponent<RectTransform>(), 0, 0, y + 29, 38);
            input.characterLimit = 1000;
            return input;
        }

        private Toggle SettingToggle(Transform parent, string name, string caption, float y)
        {
            var go = Object(name, parent);
            Top(go.GetComponent<RectTransform>(), 0, 0, y, 34);
            go.AddComponent<Image>().color = Color.clear;
            var toggle = go.AddComponent<Toggle>();
            toggle.navigation = new Navigation { mode = Navigation.Mode.None };
            var box = Object("Box", go.transform);
            Top(box.GetComponent<RectTransform>(), 0, -1, 2, 28, 28);
            var boxImage = box.AddComponent<Image>();
            boxImage.color = Field;
            var tick = Object("Tick", box.transform);
            Stretch(tick.GetComponent<RectTransform>(), 5, 5, 5, 5);
            var tickImage = tick.AddComponent<Image>();
            tickImage.color = new Color(0.36f, 0.72f, 0.79f, 1);
            toggle.targetGraphic = boxImage;
            toggle.graphic = tickImage;
            var label = Label("Label", go.transform, caption, 18);
            Top(label.rectTransform, 42, 0, 0, 34);
            return toggle;
        }

        private TMP_InputField MakeInput(string name, Transform parent, string placeholder, bool multiline)
        {
            var go = Object(name, parent);
            var background = go.AddComponent<Image>();
            background.color = Field;
            var viewport = Object("TextArea", go.transform);
            Stretch(viewport.GetComponent<RectTransform>(), 10, 10, 7, 7);
            viewport.AddComponent<RectMask2D>();
            TMP_Text value = Label("Text", viewport.transform, "", 19);
            Stretch(value.rectTransform, 0, 0, 0, 0);
            TMP_Text hint = Label("Placeholder", viewport.transform, placeholder, 18);
            Stretch(hint.rectTransform, 0, 0, 0, 0);
            hint.color = new Color(0.42f, 0.55f, 0.60f, 1);
            value.enableWordWrapping = multiline;
            var input = go.AddComponent<TMP_InputField>();
            input.targetGraphic = background;
            input.textViewport = viewport.GetComponent<RectTransform>();
            input.textComponent = value;
            input.richText = false;
            input.navigation = new Navigation { mode = Navigation.Mode.None };
            input.placeholder = hint;
            input.fontAsset = font;
            input.lineType = multiline ? TMP_InputField.LineType.MultiLineNewline : TMP_InputField.LineType.SingleLine;
            input.caretColor = Ink;
            input.customCaretColor = true;
            input.selectionColor = new Color(0.2f, 0.5f, 0.6f, 0.5f);
            return input;
        }

        private Button MakeButton(string caption, Transform parent, UnityEngine.Events.UnityAction action)
        {
            var go = Object(caption, parent);
            var image = go.AddComponent<Image>();
            image.color = Accent;
            var button = go.AddComponent<Button>();
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.targetGraphic = image;
            var colors = button.colors;
            colors.highlightedColor = new Color(0.75f, 0.91f, 1f, 1);
            colors.pressedColor = new Color(0.5f, 0.65f, 0.76f, 1);
            colors.disabledColor = new Color(0.38f, 0.42f, 0.45f, 0.75f);
            button.colors = colors;
            button.onClick.AddListener(action);
            var label = Label("Label", go.transform, caption, 18);
            Stretch(label.rectTransform, 4, 4, 0, 0);
            label.alignment = TextAlignmentOptions.Center;
            return button;
        }

        private TMP_Text Label(string name, Transform parent, string text, float size)
        {
            var go = Object(name, parent);
            var label = go.AddComponent<TextMeshProUGUI>();
            label.font = font;
            label.fontSize = size;
            label.color = Ink;
            label.text = text;
            label.richText = false;
            label.raycastTarget = false;
            label.enableWordWrapping = true;
            label.alignment = TextAlignmentOptions.TopLeft;
            return label;
        }

        private static TMP_FontAsset FindGameFont()
        {
            foreach (TMP_Text text in Resources.FindObjectsOfTypeAll<TMP_Text>())
                if (text != null && text.font != null && text.font.HasCharacter('聊', true, false)) return text.font;
            foreach (TMP_FontAsset asset in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                if (asset != null && asset.HasCharacter('聊', true, false)) return asset;
            if (TMP_Settings.defaultFontAsset != null && TMP_Settings.defaultFontAsset.HasCharacter('聊', true, false))
                return TMP_Settings.defaultFontAsset;
            throw new InvalidOperationException("FreeWorldTalk requires a loaded game TMP font with Chinese glyphs. Open a game screen before opening the mod window.");
        }

        private static GameObject Object(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void Stretch(RectTransform rect, float left, float right, float top, float bottom)
        {
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left, bottom); rect.offsetMax = new Vector2(-right, -top);
        }

        private static void Top(RectTransform rect, float left, float right, float top, float height, float width = 0)
        {
            rect.anchorMin = new Vector2(left < 0 ? 1 : 0, 1);
            rect.anchorMax = new Vector2(right < 0 ? 0 : 1, 1);
            rect.pivot = new Vector2(left < 0 ? 1 : 0, 1);
            if (left < 0) { rect.anchoredPosition = new Vector2(-right, -top); rect.sizeDelta = new Vector2(width, height); }
            else if (right < 0) { rect.anchoredPosition = new Vector2(left, -top); rect.sizeDelta = new Vector2(width, height); }
            else { rect.offsetMin = new Vector2(left, -top - height); rect.offsetMax = new Vector2(-right, -top); }
        }

        private static void Bottom(RectTransform rect, float left, float right, float bottom, float height, float width = 0)
        {
            rect.anchorMin = new Vector2(left < 0 ? 1 : 0, 0);
            rect.anchorMax = new Vector2(right < 0 ? 0 : 1, 0);
            rect.pivot = new Vector2(left < 0 ? 1 : 0, 0);
            if (left < 0) { rect.anchoredPosition = new Vector2(-right, bottom); rect.sizeDelta = new Vector2(width, height); }
            else if (right < 0) { rect.anchoredPosition = new Vector2(left, bottom); rect.sizeDelta = new Vector2(width, height); }
            else { rect.offsetMin = new Vector2(left, bottom); rect.offsetMax = new Vector2(-right, bottom + height); }
        }

        private void OnEnable() { RefreshQuickAccess(); }
        private void OnDisable()
        {
            if (runtime != null && IsOpen) Hide();
            if (quickOverlay != null) quickOverlay.SetActive(false);
        }
        private void OnDestroy()
        {
            CancelRolePromptGeneration();
            if (runtime != null) runtime.CancelPlayer();
            if (overlay != null) Destroy(overlay);
            if (quickOverlay != null) Destroy(quickOverlay);
        }
    }
}
