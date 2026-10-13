using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Script.FairyZGame.FairyUI.ViewMain.MainUI;
using TMPro;
using UnityEngine;

namespace FreeWorldTalk
{
    public sealed class TalkRuntime : MonoBehaviour
    {
        internal const string HarmonyId = "catstudio.freeworldtalk";
        public static TalkRuntime Instance { get; private set; }
        public TalkSettings Settings { get; private set; }
        public MemoryStore Store { get; private set; }
        public string Status { get; private set; } = "正在初始化";
        public string DataPath { get; private set; }
        public string PromptDirectory => promptScope == null ? Path.Combine(DataPath, "roles") : prompts.DirectoryFor(promptScope);
        public string GlobalPromptPath => prompts.GlobalPath;
        public BaseHero SelectedHero { get; private set; }
        public bool HasSelectedTalkHero => IsSelectedGroundHero(SelectedHero);
        public bool Busy => active != null;
        public bool PlayerBusy => active != null && activeMode == "player";
        public bool RolePromptBusy => active != null && activeMode == "role_prompt";
        public bool IsWindowOpen => window != null && window.IsOpen;

        private readonly ConcurrentQueue<Action> mainActions = new ConcurrentQueue<Action>();
        private readonly ConditionalWeakTable<object, SaveCapture> saveCaptures = new ConditionalWeakTable<object, SaveCapture>();
        private readonly HashSet<string> reportedErrors = new HashSet<string>();
        private readonly object worldGate = new object();
        private SettingsStore settingsStore;
        private PromptStore prompts;
        private string promptScope;
        private float nextRoleScan;
        private readonly HashSet<long> registeredPromptRoles = new HashSet<long>();
        private readonly DeepSeekClient client = new DeepSeekClient();
        private TalkWindow window;
        private CancellationTokenSource active;
        private string activeMode;
        private int requestSerial;
        private int contextGeneration;
        private FWGameData worldData;
        private UserFWStarSpread mapData;
        private bool worldReady;
        private bool automaticHealthy = true;
        private float nextPoll, nextAutomatic, nextWindowAttempt;
        private SocialPlan pendingSocial, playingSocial, capturingSocial;
        private float nextPlaybackPoll;

        private void Awake() { Instance = this; }

        public void Initialize(string modRoot)
        {
            DataPath = Path.Combine(Application.persistentDataPath, "FreeWorldTalk");
            settingsStore = new SettingsStore(DataPath);
            Settings = settingsStore.Load();
            Store = new MemoryStore(DataPath, Settings);
            prompts = new PromptStore(DataPath, modRoot);
            HarmonyHooks.Install();
            Status = string.IsNullOrWhiteSpace(Settings.ApiKey) ? "请按 F8 配置 DeepSeek API Key。" : "FreeWorldTalk 已加载；选中地面角色后按 F8 对话。";
            Logger.LogEngine("[FreeWorldTalk] 插件已加载；数据目录=" + DataPath + "；Harmony=" + HarmonyId);
        }

        private void Update()
        {
            for (int i = 0; i < 24 && mainActions.TryDequeue(out var action); i++)
                try { action(); } catch (Exception e) { Fail("主线程回调", e); }
            if (Store == null) return;
            if (playingSocial != null && Time.unscaledTime >= nextPlaybackPoll)
            {
                nextPlaybackPoll = Time.unscaledTime + 0.25f;
                PumpWorkingChat();
            }
            if (Time.unscaledTime >= nextPoll)
            {
                nextPoll = Time.unscaledTime + 0.5f;
                try { EnsureWorld(); } catch (Exception e) { Fail("存档上下文", e); }
            }
            if (window == null && Time.unscaledTime >= nextWindowAttempt)
            {
                nextWindowAttempt = Time.unscaledTime + 2;
                // DLLs load before the cover UI's fonts. Wait for that lifecycle boundary.
                var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                if (fonts.Any(f => f != null && f.HasCharacter('聊', true)))
                {
                    try { window = gameObject.AddComponent<TalkWindow>(); window.Initialize(this); }
                    catch (Exception e)
                    {
                        if (window != null) Destroy(window);
                        window = null;
                        Fail("UI初始化", e);
                    }
                }
            }
            // Use the game's selection list, including deselection and Ctrl/Shift changes.
            // The hero information view retains its last hero after it is hidden.
            var selected = Handler_RoleSelect.selectedRoles.Count == 1 ? Handler_RoleSelect.selectedRoles[0] as BaseHero : null;
            if (!IsSelectedGroundHero(selected)) selected = null;
            if (SelectedHero != selected) SetSelectedHero(selected);
            if (window != null) window.RefreshQuickAccess();
            if (Input.GetKeyDown(KeyCode.F8)) ToggleWindow();
            if (Input.GetKeyDown(KeyCode.Escape) && IsWindowOpen) window.Hide();
            if (worldReady && Time.unscaledTime >= nextRoleScan)
            {
                nextRoleScan = Time.unscaledTime + 5;
                try { EnsureEmployeePrompts(); }
                catch (Exception e) { Fail("创建雇员角色提示词", e); }
            }
        }

        private bool EnsureWorld()
        {
            lock (worldGate) return EnsureWorldLocked();
        }

        private bool EnsureWorldLocked()
        {
            var user = UserBean.tryGetInstance();
            var data = user?.gameData;
            if (!ReferenceEquals(data, worldData))
            {
                contextGeneration++;
                CancelAll("切换存档，旧请求已取消。");
                worldData = data;
                mapData = null;
                worldReady = false;
                promptScope = null;
                registeredPromptRoles.Clear();
                SelectedHero = null;
                if (window != null) window.Hide();
                Store.Clear();
                if (data == null) SetStatus("尚未加载游戏存档。");
            }
            if (data == null) return false;
            if (!worldReady)
            {
                // New-game generation publishes gameData on its worker before ArchiveGen
                // builds an Archive and UserBean.enterArchive binds it. A retained archive
                // can also belong to the previous world. Neither is ready for Mod storage.
                var archive = user.archive;
                if (archive == null || !ReferenceEquals(archive.gameData, data))
                {
                    const string waiting = "正在生成或加载存档，等待档案绑定后启用对话。";
                    if (Status != waiting) SetStatus(waiting);
                    return false;
                }
                if (archive.info == null)
                    throw new InvalidOperationException("当前存档已绑定游戏数据，但缺少元信息（Archive.info）。");
                // Retry until initialization succeeds even when gameData has not changed.
                // Only commit readiness after both checkpoint and prompt identity load.
                Store.LoadCheckpoint(GameSnapshot.CheckpointKey(archive.info));
                promptScope = prompts.BindWorld(GameSnapshot.PromptWorldKey(archive.info));
                worldReady = true;
                automaticHealthy = true;
                reportedErrors.Clear();
                nextAutomatic = Time.unscaledTime;
                nextRoleScan = Time.unscaledTime;
                SetStatus("已载入本存档的对话记忆。选中角色后按 F8。");
            }
            var map = user?.curStarMap;
            if (!ReferenceEquals(map, mapData))
            {
                contextGeneration++;
                CancelAll("切换地图，旧请求已取消。");
                mapData = map;
                SelectedHero = null;
                if (window != null) window.Hide();
            }
            return worldReady && worldData != null;
        }

        public void SetSelectedHero(BaseHero hero)
        {
            EnsureWorld();
            if (SelectedHero != hero) CancelPlayer();
            SelectedHero = hero;
            if (window != null) window.Refresh();
        }

        private bool IsSelectedGroundHero(BaseHero hero)
        {
            return hero != null && hero.heroBean != null && !hero.heroBean.isDead() && IsCurrentRole(hero.heroBean)
                && mapData?.starMapInfo != null && hero.heroBean.starMapId == mapData.starMapInfo.starMapId
                && !RootUtils.isSwitchSceneing && Handler_RoleSelect.selectedRoles.Count == 1
                && ReferenceEquals(Handler_RoleSelect.selectedRoles[0], hero);
        }

        public void ToggleWindow()
        {
            if (window == null) { SetStatus("游戏中文字体尚未加载，请等待主界面加载后再按 F8。"); return; }
            if (window.IsOpen) window.Hide();
            else if (HasSelectedTalkHero) window.Open(SelectedHero);
            else window.OpenSettings();
        }

        public bool IsCurrentRole(FWHeroBean hero)
        {
            return worldReady && hero != null && ReferenceEquals(worldData, UserBean.tryGetInstance()?.gameData)
                && worldData.heros.Contains(hero);
        }

        public void OpenRolePrompt(BaseHero hero) => OpenRolePrompt(hero?.heroBean);

        public void OpenRolePrompt(FWHeroBean hero)
        {
            try
            {
                if (!EnsureWorld() || !IsCurrentRole(hero)) throw new InvalidOperationException("该角色不属于当前已加载存档。");
                if (window == null) throw new InvalidOperationException("角色提示词编辑窗口尚未加载。");
                window.OpenRolePrompt(hero);
            }
            catch (Exception e) { Fail("打开角色提示词", e); }
        }

        public string GetRolePrompt(FWHeroBean hero)
        {
            if (!EnsureWorld() || !IsCurrentRole(hero)) throw new InvalidOperationException("无法读取其他存档的角色提示词。");
            return prompts.EnsureRole(promptScope, GameSnapshot.Background(hero), prompts.LoadGlobal());
        }

        public void OpenRoleMemory(FWHeroBean hero)
        {
            try
            {
                if (!CanEditRoleMemory(hero)) return;
                if (window == null) { SetStatus("角色记忆窗口尚未加载。"); return; }
                window.OpenRoleMemory(hero);
            }
            catch (Exception error) { Fail("打开角色记忆", error); }
        }

        public List<CharacterMemoryRecord> GetRoleMemories(FWHeroBean hero)
        {
            if (!EnsureWorld() || !IsCurrentRole(hero)) throw new InvalidOperationException("无法读取其他存档的角色记忆。");
            return Store.RecordsFor(hero.uid, worldData.elapsedMM);
        }

        public bool EditRoleMemory(FWHeroBean hero, CharacterMemoryRecord expected, string text)
        {
            try
            {
                if (!CanEditRoleMemory(hero) || !ValidMemoryText(text)) return false;
                if (!Store.TryEditRecord(hero.uid, expected, text, worldData.elapsedMM, out string reason))
                { SetStatus(reason); return false; }
                RoleMemoryChanged("该条角色记忆已修改。");
                return true;
            }
            catch (Exception error) { Fail("编辑角色记忆", error); return false; }
        }

        public bool DeleteRoleMemory(FWHeroBean hero, CharacterMemoryRecord expected)
        {
            try
            {
                if (!CanEditRoleMemory(hero)) return false;
                if (!Store.TryDeleteRecord(hero.uid, expected, worldData.elapsedMM, out string reason))
                { SetStatus(reason); return false; }
                RoleMemoryChanged("该条角色记忆已删除。");
                return true;
            }
            catch (Exception error) { Fail("删除角色记忆", error); return false; }
        }

        public bool AddRoleMemory(FWHeroBean hero, string text)
        {
            try
            {
                if (!CanEditRoleMemory(hero) || !ValidMemoryText(text)) return false;
                Store.AddManualMemory(hero.uid, hero.Name, text, worldData.elapsedMM);
                RoleMemoryChanged("该角色的新记忆已添加。");
                return true;
            }
            catch (Exception error) { Fail("添加角色记忆", error); return false; }
        }

        private bool CanEditRoleMemory(FWHeroBean hero)
        {
            if (EnsureWorld() && IsCurrentRole(hero)) return true;
            SetStatus("该角色已离开当前存档，请重新打开角色记忆。");
            return false;
        }

        private bool ValidMemoryText(string text)
        {
            if (!string.IsNullOrWhiteSpace(text) && text.Length <= MemoryStore.MaxRecordCharacters) return true;
            SetStatus("记忆内容应为 1–" + MemoryStore.MaxRecordCharacters + " 字符。");
            return false;
        }

        private void RoleMemoryChanged(string message)
        {
            CancelAll("角色记忆已更新，旧请求已取消。");
            SetStatus(message + "后续对话使用新内容；请保存游戏以写入当前存档。");
        }

        public bool GenerateRolePrompt(FWHeroBean hero, Action<RolePromptResponse> completed)
        {
            try
            {
                if (completed == null) throw new ArgumentNullException(nameof(completed));
                if (!EnsureWorld() || !IsCurrentRole(hero)) throw new InvalidOperationException("无法为其他存档的角色生成提示词。");
                if (RolePromptBusy) { SetStatus("角色提示词正在生成，请等待或取消。"); return false; }
                if (string.IsNullOrWhiteSpace(Settings.ApiKey)) { SetStatus("请先在设置中填写 DeepSeek API Key。"); return false; }
                var config = prompts.LoadRoleGenerator();
                // Gather Unity/game facts now. The worker receives only detached strings and DTOs.
                var request = new RolePromptRequest {
                    Actor = GameSnapshot.RolePromptProfile(hero), SystemPrompt = config.SystemPrompt,
                    RequestInstruction = config.RequestInstruction
                };
                var settings = JsonConvert.DeserializeObject<TalkSettings>(JsonConvert.SerializeObject(Settings));
                settings.ApiKey = Settings.ApiKey;
                settings.MaxOutputTokens = config.MaxOutputTokens;
                DeepSeekClient.ValidateRolePromptRequest(request);
                SettingsStore.Validate(settings);
                CancelAll("角色提示词生成优先，旧请求已取消。");
                var source = new CancellationTokenSource();
                var token = source.Token;
                int serial = ++requestSerial;
                int generation = contextGeneration;
                var data = worldData;
                var map = mapData;
                long uid = hero.uid;
                active = source;
                activeMode = "role_prompt";
                SetStatus("正在请 DeepSeek 根据角色资料生成提示词…");
                Task.Run(async () =>
                {
                    try
                    {
                        var response = await client.GenerateRolePromptAsync(request, settings, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        mainActions.Enqueue(() =>
                        {
                            if (this == null || serial != requestSerial) return;
                            CompleteRequest(source);
                            if (generation != contextGeneration || !ReferenceEquals(data, UserBean.tryGetInstance()?.gameData)
                                || !ReferenceEquals(map, UserBean.tryGetInstance()?.curStarMap) || RootUtils.isSwitchSceneing || !IsCurrentRole(hero))
                            { SetStatus("生成结果已过期：角色或存档发生变化。"); return; }
                            SetStatus("角色提示词已生成；确认草稿后点击保存。");
                            completed(response);
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        mainActions.Enqueue(() => {
                            if (this == null || serial != requestSerial) return;
                            CompleteRequest(source);
                            SetStatus("角色提示词生成已取消，原草稿保留。");
                        });
                    }
                    catch (Exception error)
                    {
                        mainActions.Enqueue(() => {
                            if (this == null || serial != requestSerial) return;
                            CompleteRequest(source);
                            Fail("生成角色提示词 UID=" + uid, error);
                        });
                    }
                });
                return true;
            }
            catch (Exception error) { Fail("准备生成角色提示词", error); return false; }
        }

        public void CancelRolePrompt()
        {
            if (!RolePromptBusy) return;
            CancelAll("角色提示词生成已取消，原草稿保留。");
        }

        public string GetCommonRolePrompt() => prompts.LoadGlobal().CommonRolePrompt;

        public bool SaveCommonRolePrompt(string text)
        {
            try
            {
                prompts.SaveCommonRole(text);
                CancelAll("通用角色提示词已更新，旧请求已取消。");
                automaticHealthy = true;
                SetStatus("通用角色提示词已保存；所有角色的后续对话使用新内容。");
                return true;
            }
            catch (Exception e) { Fail("保存通用角色提示词", e); return false; }
        }

        public bool SaveRolePrompt(FWHeroBean hero, string text)
        {
            try
            {
                if (!EnsureWorld() || !IsCurrentRole(hero)) throw new InvalidOperationException("无法修改其他存档的角色提示词。");
                prompts.SaveRole(promptScope, hero.uid, text);
                CancelAll("角色提示词已更新，旧请求已取消。");
                automaticHealthy = true;
                SetStatus(hero.Name + " 的角色提示词已保存；下一次对话生效。");
                return true;
            }
            catch (Exception e) { Fail("保存角色提示词", e); return false; }
        }

        public void ReloadPrompts()
        {
            CancelAll("重载提示词，旧请求已取消。");
            try
            {
                var config = prompts.LoadGlobal();
                if (EnsureWorld())
                    foreach (var hero in worldData.heros.Where(IsEmployee))
                        prompts.EnsureRole(promptScope, GameSnapshot.Background(hero), config);
                automaticHealthy = true;
                SetStatus("提示词 JSON 已重新读取；未保存的编辑草稿仍保留。");
            }
            catch (Exception e) { automaticHealthy = false; Fail("重载提示词 JSON", e); }
        }

        private static bool IsEmployee(FWHeroBean hero) => hero != null && !hero.isDead() && !hero.prisoner
            && hero.allianceID == RoleAlliance.Alliance_MINE;

        private void EnsureEmployeePrompts()
        {
            var missing = worldData.heros.Where(IsEmployee).Where(h => !registeredPromptRoles.Contains(h.uid)).ToList();
            if (missing.Count == 0) return;
            var config = prompts.LoadGlobal();
            foreach (var hero in missing)
            {
                prompts.EnsureRole(promptScope, GameSnapshot.Background(hero), config);
                registeredPromptRoles.Add(hero.uid);
            }
        }

        public void ApplySettings(TalkSettings updated)
        {
            try
            {
                SettingsStore.Validate(updated);
                settingsStore.Save(updated);
                CancelAll("配置已更新，旧请求已取消。");
                // MemoryStore retains this settings instance, so update it in place.
                JsonConvert.PopulateObject(JsonConvert.SerializeObject(updated), Settings);
                Settings.ApiKey = updated.ApiKey;
                automaticHealthy = true;
                nextAutomatic = Time.unscaledTime;
                SetStatus(string.IsNullOrWhiteSpace(Settings.ApiKey) ? "配置已保存；仍需填写 API Key。" : "DeepSeek 配置已保存。尚未发起连接请求。");
            }
            catch (Exception e) { Fail("保存配置", e); }
        }

        public void ClearMemory()
        {
            if (!worldReady) { SetStatus("尚未加载存档。"); return; }
            CancelAll("清空记忆，旧请求已取消。");
            Store.Clear();
            SetStatus("当前存档对话记忆已清空；保存游戏后写入对应存档，旧检查点仍保留。");
        }

        public void SendPlayer(string text)
        {
            try
            {
                if (!EnsureWorld()) { SetStatus("请先加载游戏存档。"); return; }
                if (string.IsNullOrWhiteSpace(text) || text.Length > DeepSeekClient.MaxPlayerCharacters)
                { SetStatus("请输入 1–" + DeepSeekClient.MaxPlayerCharacters + " 字符的消息。"); return; }
                if (!GameSnapshot.CanTalk(SelectedHero, out string reason)) { SetStatus("无法对话：" + reason); return; }
                if (string.IsNullOrWhiteSpace(Settings.ApiKey)) { SetStatus("请先在设置中填写 DeepSeek API Key。"); return; }
                var hero = SelectedHero;
                var req = new DialogueRequest {
                    Mode = "player", Topic = "玩家与角色对话", PlayerText = text.Trim(), MapContext = GameSnapshot.Map(),
                    Actors = new List<ActorSnapshot> { GameSnapshot.Actor(hero, Settings.UseCharacterProfile) },
                    Memory = Store.ContextFor(new[] { hero.heroBean.uid }, worldData.elapsedMM)
                };
                CancelAll("玩家对话优先，取消尚未开始的自动闲聊生成。");
                StartRequest(req, response =>
                {
                    if (SelectedHero != hero || !GameSnapshot.CanTalk(hero, out reason))
                    { SetStatus("回复已过期：角色状态或选择发生变化。"); return; }
                    Store.RememberPlayer(hero.heroBean.uid, hero.heroBean.Name, req.PlayerText, response.Lines[0].Text, worldData.elapsedMM);
                    SetStatus(response.FromCache ? "回复完成（本地缓存）。" : "回复完成；token=" + (response.PromptTokens + response.CompletionTokens));
                }, e => Fail("玩家对话", e));
            }
            catch (Exception e) { Fail("准备玩家对话", e); }
        }

        public void CancelPlayer()
        {
            if (activeMode == "player") CancelAll("玩家对话请求已取消。");
        }

        private void CancelAll(string reason)
        {
            requestSerial++;
            var previous = active;
            active = null;
            activeMode = null;
            pendingSocial = null;
            CancelWorkingChat();
            if (previous != null) { previous.Cancel(); previous.Dispose(); SetStatus(reason); }
        }

        private void StartRequest(DialogueRequest request, Action<DialogueResponse> completed, Action<Exception> failed)
        {
            var config = prompts.LoadGlobal();
            foreach (var actor in request.Actors)
            {
                var hero = worldData.heros.FirstOrDefault(h => h.uid == actor.Uid);
                if (hero == null) throw new InvalidOperationException("请求角色已离开当前存档：" + actor.Uid);
                prompts.EnsureRole(promptScope, GameSnapshot.Background(hero), config);
            }
            // Freeze a separate request for each speaker on the main thread. Other participants'
            // editable role instructions never enter this speaker's system prompt.
            long[] speakers = request.Mode == "social" ? request.SeedLines.Select(line => line.SpeakerUid).Distinct().ToArray()
                : new[] { request.Actors[0].Uid };
            var speakerRequests = speakers.Select(uid => new DialogueRequest {
                Mode = request.Mode, SpeakerUid = uid, Topic = request.Topic, MapContext = request.MapContext,
                Memory = request.Memory, PlayerText = request.PlayerText, Actors = request.Actors, SeedLines = request.SeedLines
            }).ToArray();
            foreach (var speakerRequest in speakerRequests)
            {
                prompts.PrepareRequest(speakerRequest, Settings, promptScope, config);
                DeepSeekClient.ValidateRequest(speakerRequest);
            }
            var settings = JsonConvert.DeserializeObject<TalkSettings>(JsonConvert.SerializeObject(Settings));
            settings.ApiKey = Settings.ApiKey;
            var source = new CancellationTokenSource();
            var token = source.Token;
            int serial = ++requestSerial;
            var data = worldData;
            var map = mapData;
            var store = Store;
            active = source;
            activeMode = request.Mode;
            SetStatus(request.Mode == "player" ? "正在请求 DeepSeek…" : "正在生成自动闲聊；角色继续原工作。");
            Task.Run(async () =>
            {
                try
                {
                    var results = await Task.WhenAll(speakerRequests.Select(async speakerRequest => {
                        token.ThrowIfCancellationRequested();
                        DialogueResponse part;
                        bool cached = store.TryGetCache(speakerRequest, settings, out part);
                        if (!cached) part = await client.GenerateAsync(speakerRequest, settings, token).ConfigureAwait(false);
                        return Tuple.Create(speakerRequest, part, cached);
                    })).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    var response = new DialogueResponse { FromCache = results.All(result => result.Item3) };
                    foreach (var result in results)
                    {
                        response.PromptTokens = checked(response.PromptTokens + result.Item2.PromptTokens);
                        response.CompletionTokens = checked(response.CompletionTokens + result.Item2.CompletionTokens);
                    }
                    if ((long)response.PromptTokens + response.CompletionTokens > int.MaxValue)
                        throw new InvalidOperationException("合并对话 token 用量超过范围。");
                    if (request.Mode == "social")
                    {
                        var linesBySpeaker = results.ToDictionary(result => result.Item1.SpeakerUid,
                            result => new Queue<TalkLine>(result.Item2.Lines));
                        foreach (var seed in request.SeedLines) response.Lines.Add(linesBySpeaker[seed.SpeakerUid].Dequeue());
                    }
                    else response.Lines.AddRange(results[0].Item2.Lines);
                    mainActions.Enqueue(() =>
                    {
                        if (this == null || serial != requestSerial) return;
                        CompleteRequest(source);
                        if (!ReferenceEquals(data, UserBean.tryGetInstance()?.gameData) || !ReferenceEquals(map, UserBean.tryGetInstance()?.curStarMap) || RootUtils.isSwitchSceneing)
                        { pendingSocial = null; SetStatus("回复已过期：游戏上下文发生变化。"); return; }
                        completed(response);
                        // Cache errors do not erase a successfully delivered response.
                        if (results.Any(result => !result.Item3)) Task.Run(() => {
                            try {
                                foreach (var result in results.Where(result => !result.Item3))
                                    store.PutCache(result.Item1, settings, result.Item2);
                            }
                            catch (Exception error) { mainActions.Enqueue(() => { if (this != null) Fail("写入本地缓存", error); }); }
                        });
                    });
                }
                catch (OperationCanceledException)
                {
                    mainActions.Enqueue(() => { if (this != null && serial == requestSerial) { CompleteRequest(source); pendingSocial = null; SetStatus("请求已取消。"); } });
                }
                catch (Exception e)
                {
                    mainActions.Enqueue(() => {
                        if (this == null || serial != requestSerial) return;
                        CompleteRequest(source);
                        if (ReferenceEquals(data, UserBean.tryGetInstance()?.gameData) && ReferenceEquals(map, UserBean.tryGetInstance()?.curStarMap)) failed(e);
                        else { pendingSocial = null; SetStatus("旧场景请求失败，已丢弃。"); }
                    });
                }
            });
        }

        private void CompleteRequest(CancellationTokenSource source)
        {
            if (ReferenceEquals(active, source)) { active = null; activeMode = null; }
            source.Dispose();
        }

        internal bool BeforeSocial(AI_Base ai, ChattingDecisionDef def, List<BaseHero> targets, float rate)
        {
            // Once enabled, every automatic trigger is consumed: no original Social Action is pushed,
            // including when a request is waiting, rate limited, or unavailable.
            if (Store == null || !Settings.AutoEnabled) return true;
            if (!automaticHealthy) return false;
            try
            {
                if (!EnsureWorld()) return false;
                var hero = ai.fromRole as BaseHero;
                if (def == null || targets == null || targets.Count == 0) return false;
                if (Busy || pendingSocial != null || playingSocial != null || Time.unscaledTime < nextAutomatic || IsWindowOpen) return false;
                if (!GameSnapshot.CanTalk(hero, out string reason)) return false;
                var members = new List<BaseHero> { hero };
                members.AddRange(targets.Take(Math.Max(0, def.MemberCount - 1)));
                if (members.Count < 2 || members.Any(h => !GameSnapshot.CanTalk(h, out reason))) return false;
                nextAutomatic = Time.unscaledTime + Settings.AutoIntervalSeconds;
                var plan = PreparePlan(ai, def, members, rate);
                if (plan.Lines.Count == 0) { SetStatus("本次闲聊没有符合角色条件的原表台词，已跳过。"); return false; }
                if (plan.Lines.Count > 16) { SetStatus("本次原表闲聊超过 16 句，已跳过以限制请求长度。"); return false; }
                if (!CheckSocial(plan, out reason)) return false;
                if (string.IsNullOrWhiteSpace(Settings.ApiKey))
                {
                    StartWorkingChat(plan);
                    SetStatus("未配置 API Key，本次使用原表台词边工作边聊。");
                    return false;
                }
                var req = new DialogueRequest {
                    Mode = "social", Topic = def.Name, MapContext = GameSnapshot.Map(),
                    Actors = members.Select(member => GameSnapshot.Actor(member, Settings.UseCharacterProfile)).ToList(), SeedLines = ReadLines(plan.Lines),
                    Memory = Store.ContextFor(members.Select(h => h.heroBean.uid).ToArray(), worldData.elapsedMM)
                };
                pendingSocial = plan;
                StartRequest(req, response => {
                    pendingSocial = null;
                    if (!CheckSocial(plan, out reason)) { SetStatus("闲聊已过期：" + reason); return; }
                    for (int i = 0; i < plan.Lines.Count; i++) HarmonyHooks.LineText.SetValue(plan.Lines[i], response.Lines[i].Text);
                    StartWorkingChat(plan);
                    SetStatus(response.FromCache ? "工作中闲聊已开始（本地缓存）。" : "AI 工作中闲聊已开始。");
                }, error => {
                    pendingSocial = null;
                    Fail("自动闲聊生成", error);
                    if (CheckSocial(plan, out reason)) { StartWorkingChat(plan); SetStatus("AI 生成失败，本次使用原表台词边工作边聊；原因：" + SafeMessage(error)); }
                });
                return false;
            }
            catch (Exception e)
            {
                automaticHealthy = false;
                Fail("自动闲聊接入已停用", e);
                return false;
            }
        }

        private SocialPlan PreparePlan(AI_Base ai, ChattingDecisionDef def, List<BaseHero> members, float rate)
        {
            var plan = new SocialPlan { Def = def, Members = members, ParticipantBeans = members.Select(h => h.heroBean).ToList(),
                World = worldData, Map = mapData, ContextGeneration = contextGeneration };
            var draft = new LogRecord_Chat(def, rate);
            var original = members.Select(h => h.commonValue).ToArray();
            try
            {
                foreach (var member in members) draft.addMembers(member);
                HarmonyHooks.BuildLines.Invoke(draft, null);
                var lines = (IList)HarmonyHooks.ChatLines.GetValue(draft);
                foreach (var line in lines) plan.Lines.Add(line);
            }
            finally
            {
                for (int i = 0; i < members.Count; i++) members[i].commonValue = original[i];
            }
            return plan;
        }

        private bool CheckSocial(SocialPlan plan, out string reason, bool requireNearby = true)
        {
            if (!MatchesSocialContext(plan)) { reason = "存档或地图已切换"; return false; }
            foreach (var member in plan.Members)
            {
                if (!GameSnapshot.CanTalk(member, out reason)) return false;
                if (member.getAlliance() != plan.Members[0].getAlliance()) { reason = "参与者阵营变化"; return false; }
                if (requireNearby && member != plan.Members[0] && (MapTools.getDirectReachCost(plan.Members[0].cellX, plan.Members[0].cellY, member.cellX, member.cellY) > 6 || !SightTools.isInSight(plan.Members[0], member)))
                { reason = "参与者已离开交流范围"; return false; }
            }
            reason = "";
            return true;
        }

        private void StartWorkingChat(SocialPlan plan)
        {
            if (playingSocial != null) throw new InvalidOperationException("工作闲聊播放器已有未完成会话。");
            playingSocial = plan;
            plan.Log = new LogRecord_Chat(plan.Def, 0);
            PlayWorkingLine(plan);
        }

        // addChatToast returns void. This scoped hook captures only the bubble this Mod just requested,
        // allowing cancellation to return that bubble to its original owner's pool.
        internal void OnWorkingToastShown(ChatToast toast, BaseHero hero)
        {
            var plan = capturingSocial;
            if (plan == null || !ReferenceEquals(playingSocial, plan)) return;
            if (plan.Toast != null || hero != (BaseHero)HarmonyHooks.LineSpeaker.GetValue(plan.Lines[plan.NextIndex]))
                throw new InvalidOperationException("工作闲聊气泡的所有权或发言者不匹配。");
            plan.Toast = toast;
        }

        private void PlayWorkingLine(SocialPlan plan)
        {
            if (!ReferenceEquals(playingSocial, plan)) return;
            if (!CheckSocial(plan, out string reason, false)) { EndWorkingChat(plan, "工作闲聊已结束：" + reason); return; }
            if (plan.NextIndex >= plan.Lines.Count) { EndWorkingChat(plan, null); return; }
            var manager = Info_RoleManager.getInstance();
            if (manager == null) { EndWorkingChat(plan, "地面气泡界面已卸载，工作闲聊结束。"); return; }
            int index = plan.NextIndex;
            var line = ReadLine(plan.Lines[index]);
            var speaker = (BaseHero)HarmonyHooks.LineSpeaker.GetValue(plan.Lines[index]);
            plan.Owner = manager;
            plan.Toast = null;
            plan.BubbleElapsed = 0;
            plan.LastPoll = Time.unscaledTime;
            // The bubble now types at 0.05 real seconds per character, then holds for 2 real seconds.
            plan.BubbleTimeout = Mathf.Max(10, line.Text.Length * 0.05f + 7);
            capturingSocial = plan;
            try
            {
                manager.addChatToast(speaker, line.Text, () =>
                {
                    if (this == null || !ReferenceEquals(playingSocial, plan) || index != plan.NextIndex) return;
                    // The original manager has already returned this bubble to its pool before invoking us.
                    plan.Toast = null;
                    try
                    {
                        if (!CheckSocial(plan, out reason, false)) { EndWorkingChat(plan, "工作闲聊已结束：" + reason); return; }
                        Store.RememberSocial(new List<TalkLine> { line }, plan.Def.Name, worldData.elapsedMM,
                            plan.Members.Select(h => h.heroBean.uid).ToArray());
                        plan.Log.addContent(speaker.heroBean.Name + "：\"" + line.Text + "\"");
                        plan.NextIndex++;
                        // Deferring avoids recursion through pooled UI callbacks.
                        mainActions.Enqueue(() => { if (this != null) PlayWorkingLine(plan); });
                    }
                    catch (Exception e) { CancelWorkingChat(); Fail("完成工作闲聊台词", e); }
                });
                if (plan.Toast == null && ReferenceEquals(playingSocial, plan) && index == plan.NextIndex)
                    throw new InvalidOperationException("游戏创建了工作闲聊气泡，但 Mod 未捕获所有权。");
            }
            catch
            {
                CancelWorkingChat();
                throw;
            }
            finally { capturingSocial = null; }
        }

        private void PumpWorkingChat()
        {
            var plan = playingSocial;
            if (plan == null) return;
            try
            {
                if (!CheckSocial(plan, out string reason, false)) { EndWorkingChat(plan, "工作闲聊已结束：" + reason); return; }
                if (plan.NextIndex >= plan.Lines.Count) return;
                if (plan.Owner == null || plan.Toast == null || !plan.Toast.gameObject.activeInHierarchy)
                { EndWorkingChat(plan, "工作闲聊气泡已关闭，会话已取消。"); return; }
                float now = Time.unscaledTime;
                plan.BubbleElapsed += Mathf.Max(0, now - plan.LastPoll) * HarmonyHooks.DialogueReadingSpeed();
                plan.LastPoll = now;
                if (plan.BubbleElapsed > plan.BubbleTimeout)
                {
                    EndWorkingChat(plan, null);
                    Fail("工作闲聊气泡超时", new TimeoutException("发言气泡未在原显示周期内结束；未完成台词没有写入记忆。"));
                }
            }
            catch (Exception e) { CancelWorkingChat(); Fail("工作闲聊生命周期", e); }
        }

        private void EndWorkingChat(SocialPlan plan, string reason)
        {
            if (!ReferenceEquals(playingSocial, plan)) return;
            // Invalidate first so any late callback cannot advance a cancelled conversation.
            playingSocial = null;
            if (plan.Owner != null && plan.Toast != null && plan.Toast.gameObject.activeSelf)
                plan.Owner.removeChatToast(plan.Toast);
            plan.Toast = null;
            if (MatchesSocialContext(plan) && plan.Log != null && plan.Log.contents.Count > 0)
            {
                // Mirrors the original Social Action's completed exchange need satisfaction only.
                // No social scoring, mood effect, movement, animation, or Action is changed.
                foreach (var bean in plan.ParticipantBeans) bean.clearSocialWant();
                plan.Log.time = UserBean.getInstance().getTime();
                LogRecorder.getInstance().finishItem(plan.Log);
            }
            if (reason != null) SetStatus(reason);
        }

        private void CancelWorkingChat()
        {
            if (playingSocial != null) EndWorkingChat(playingSocial, null);
        }

        private bool MatchesSocialContext(SocialPlan plan)
        {
            var user = UserBean.tryGetInstance();
            return worldReady && plan.ContextGeneration == contextGeneration && !RootUtils.isSwitchSceneing
                && ReferenceEquals(plan.World, worldData) && ReferenceEquals(plan.World, user?.gameData)
                && ReferenceEquals(plan.Map, mapData) && ReferenceEquals(plan.Map, user?.curStarMap);
        }

        internal void OnMood(FW_Attr_MoodPart part)
        {
            if (!worldReady || !part.isRunning()) return;
            var hero = HarmonyHooks.MoodHero.GetValue(part) as FWHeroBean;
            if (hero == null || !worldData.heros.Contains(hero)) return;
            var def = part.getDef();
            if (def == null) throw new InvalidOperationException("已触发的心情事件缺少定义，id=" + part.id);
            Store.RememberEvent(hero.uid, "亲身经历的心情事件：" + def.getName() + "；" + def.getDesc(), worldData.elapsedMM);
        }

        internal SaveCapture BeforeSnapshot(Archive archive)
        {
            // Called by the game's main OR background save path. Only detached, locked Mod data is touched.
            lock (worldGate)
            {
                if (!ReferenceEquals(archive.gameData, worldData) || !worldReady) return null;
                return new SaveCapture { Store = Store, Json = Store.ExportCheckpoint(long.MaxValue), PromptScope = promptScope };
            }
        }

        internal void AfterSnapshot(object snapshot, SaveCapture capture)
        {
            if (snapshot == null || capture == null) return;
            var info = (ArchiveInfo)HarmonyHooks.SnapshotInfo.GetValue(snapshot);
            var gameData = (FWGameData)HarmonyHooks.SnapshotGameData.GetValue(snapshot);
            if (gameData == null) throw new InvalidOperationException("游戏存档快照缺少模拟数据。");
            capture.Json = capture.Store.FinalizeCheckpoint(capture.Json, gameData.elapsedMM);
            capture.Key = GameSnapshot.CheckpointKey(info);
            capture.PromptWorldKey = GameSnapshot.PromptWorldKey(info);
            saveCaptures.Add(snapshot, capture);
        }

        internal void AfterSave(object snapshot, bool success)
        {
            if (snapshot == null || !saveCaptures.TryGetValue(snapshot, out var capture)) return;
            saveCaptures.Remove(snapshot);
            if (!success) return;
            // Postfix may run on the archive writer thread: no Unity APIs here.
            try { capture.Store.WriteCheckpoint(capture.Key, capture.Json); }
            catch (Exception e) { mainActions.Enqueue(() => { if (this != null) Fail("游戏已保存，但对话记忆保存失败", e); }); }
            try { prompts.AddWorldAlias(capture.PromptWorldKey, capture.PromptScope); }
            catch (Exception e) { ReportSaveHookError("游戏已保存，但角色提示词身份保存失败", e); }
        }

        internal void ReportHookError(string stage, Exception error)
        {
            automaticHealthy = false;
            Fail(stage, error);
        }

        internal void ReportUiHookError(string stage, Exception error) => Fail(stage, error);

        internal void ReportSaveHookError(string stage, Exception error)
        {
            mainActions.Enqueue(() => { if (this != null) Fail(stage, error); });
        }

        private static TalkLine ReadLine(object line)
        {
            var hero = (BaseHero)HarmonyHooks.LineSpeaker.GetValue(line);
            return new TalkLine { SpeakerUid = hero.heroBean.uid, Text = (string)HarmonyHooks.LineText.GetValue(line) };
        }
        private static List<TalkLine> ReadLines(List<object> lines) => lines.Select(ReadLine).ToList();

        private void SetStatus(string value)
        {
            Status = value;
            if (window != null) window.Refresh();
        }
        private string SafeMessage(Exception error)
        {
            string text = (error is System.Reflection.TargetInvocationException && error.InnerException != null ? error.InnerException.Message : error.Message) ?? error.GetType().Name;
            if (!string.IsNullOrEmpty(Settings?.ApiKey)) text = text.Replace(Settings.ApiKey, "[密钥已隐藏]");
            return text.Length > 300 ? text.Substring(0, 300) : text;
        }
        private void Fail(string stage, Exception error)
        {
            string reason = SafeMessage(error);
            SetStatus(stage + "：" + reason);
            string identity = stage + "|" + reason;
            if (reportedErrors.Count > 64) reportedErrors.Clear();
            if (reportedErrors.Add(identity))
                Logger.LogErrorEngine("[FreeWorldTalk] stage=" + stage + " request=" + requestSerial + " actor=" +
                    (SelectedHero?.heroBean?.uid.ToString() ?? "none") + " map=" + (mapData?.starMapInfo?.starMapId.ToString() ?? "none") +
                    " time=" + (worldData?.elapsedMM.ToString() ?? "none") + " reason=" + reason);
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            CancelAll("插件关闭。");
            RoleInfoPromptButton.Cleanup();
            HarmonyHooks.Uninstall();
            Instance = null;
        }

        internal sealed class SaveCapture { internal MemoryStore Store; internal string Json; internal string Key; internal string PromptScope; internal string PromptWorldKey; }
        private sealed class SocialPlan
        {
            internal FWGameData World; internal UserFWStarSpread Map; internal int ContextGeneration;
            internal ChattingDecisionDef Def;
            internal List<BaseHero> Members;
            internal List<FWHeroBean> ParticipantBeans;
            internal readonly List<object> Lines = new List<object>();
            internal int NextIndex;
            internal Info_RoleManager Owner;
            internal ChatToast Toast;
            internal LogRecord_Chat Log;
            internal float BubbleElapsed, BubbleTimeout, LastPoll;
        }
    }
}
