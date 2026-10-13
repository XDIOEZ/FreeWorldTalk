using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Script.FairyZGame.FairyUI.ViewMain.MainUI;

namespace FreeWorldTalk
{
    internal static class HarmonyHooks
    {
        private static Harmony harmony;
        internal static FieldInfo ChatLines, LineText, LineSpeaker, MoodHero, SnapshotInfo, SnapshotGameData;
        internal static MethodInfo BuildLines;

        internal static void Install()
        {
            ChatLines = RequireField(typeof(LogRecord_Chat), "chatLines");
            var lineType = ChatLines.FieldType.GetGenericArguments()[0];
            LineText = RequireField(lineType, "text");
            LineSpeaker = RequireField(lineType, "speaker");
            MoodHero = RequireField(typeof(FW_Attr_MoodPart), "heroBean");
            BuildLines = RequireMethod(typeof(LogRecord_Chat), "buildChatLines");
            var snapshotMethod = RequireMethod(typeof(Archive), "createSaveSnapshot");
            SnapshotInfo = RequireField(snapshotMethod.ReturnType, "infoSnapshot");
            SnapshotGameData = RequireField(snapshotMethod.ReturnType, "gameDataSnapshot");

            harmony = new Harmony(TalkRuntime.HarmonyId);
            try
            {
                Patch(RequireMethod(typeof(AI_Base), "triggerChat"), nameof(SocialPrefix));
                Patch(RequireMethod(typeof(ChatToast), "ShowText"), postfix: nameof(WorkingToastPostfix));
                Patch(RequireMethod(typeof(ChatToast), "Update"), transpiler: nameof(DialogueTimingTranspiler));
                Patch(RequireMethod(typeof(Hero_MainView), "setHero"), postfix: nameof(SelectedPostfix));
                Patch(RequireMethod(typeof(Hero_MainView), "setHeros"), postfix: nameof(MultipleSelectedPostfix));
                Patch(RequireMethod(typeof(Dialog_MainHeroInfo), "showHero"), postfix: nameof(RoleInfoPostfix));
                Patch(RequireMethod(typeof(FW_Attr_MoodPart), "trigger"), postfix: nameof(MoodPostfix));
                Patch(RequireMethod(typeof(KeyBoardCentre), "Update"), nameof(InputPrefix));
                Patch(RequireMethod(typeof(KeyBoardEvent), "Update"), nameof(InputPrefix));
                foreach (var method in typeof(ControlManager).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name.StartsWith("Pointer", StringComparison.Ordinal) || m.Name == "clickPointer" || m.Name == "doubleClick"))
                    Patch(method, nameof(InputPrefix));
                Patch(RequireMethod(typeof(ControlManager), "triggerKeyboard"), nameof(KeyboardPrefix));
                Patch(snapshotMethod, nameof(SnapshotPrefix), nameof(SnapshotPostfix));
                Patch(RequireMethod(typeof(Archive), "createBackgroundSaveSnapshot"), nameof(SnapshotPrefix), nameof(SnapshotPostfix));
                var saveMethod = typeof(Archive).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Single(m => m.Name == "saveSnapshotFile" && m.GetParameters().Length == 6);
                Patch(saveMethod, postfix: nameof(SavePostfix));
            }
            catch
            {
                harmony.UnpatchAll(TalkRuntime.HarmonyId);
                throw;
            }
        }

        private static FieldInfo RequireField(Type type, string name) => AccessTools.Field(type, name) ??
            throw new MissingFieldException("FreeWorldTalk 不兼容当前游戏版本，缺少字段 " + type.FullName + "." + name);
        private static MethodInfo RequireMethod(Type type, string name) => AccessTools.Method(type, name) ??
            throw new MissingMethodException("FreeWorldTalk 不兼容当前游戏版本，缺少方法 " + type.FullName + "." + name);
        private static void Patch(MethodBase method, string prefix = null, string postfix = null, string transpiler = null)
        {
            harmony.Patch(method,
                prefix == null ? null : new HarmonyMethod(typeof(HarmonyHooks), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(HarmonyHooks), postfix),
                transpiler == null ? null : new HarmonyMethod(typeof(HarmonyHooks), transpiler));
        }
        internal static void Uninstall() { harmony?.UnpatchAll(TalkRuntime.HarmonyId); harmony = null; }

        private static bool SocialPrefix(AI_Base __instance, ChattingDecisionDef aimCDD, List<BaseHero> targets, float rate) =>
            TalkRuntime.Instance == null || TalkRuntime.Instance.BeforeSocial(__instance, aimCDD, targets, rate);
        private static void WorkingToastPostfix(ChatToast __instance, BaseHero hero)
        {
            TalkRuntime.Instance?.OnWorkingToastShown(__instance, hero);
        }

        private static IEnumerable<CodeInstruction> DialogueTimingTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var gameSpeed = RequireMethod(typeof(MapManager), "getNowSpeed");
            var readingSpeed = RequireMethod(typeof(HarmonyHooks), nameof(DialogueReadingSpeed));
            int replaced = 0;
            foreach (var instruction in code)
            {
                if (instruction.opcode == OpCodes.Call && Equals(instruction.operand, gameSpeed))
                {
                    // Keep the original layout, reveal logic, completion callback, and pause branch.
                    // Only this bubble's multiplier is replaced; labels and exception blocks stay intact.
                    instruction.operand = readingSpeed;
                    replaced++;
                }
            }
            if (replaced != 1)
                throw new InvalidOperationException("FreeWorldTalk 对话计时补丁不兼容：ChatToast.Update 中预期一个游戏倍速调用，实际=" + replaced);
            return code;
        }

        internal static float DialogueReadingSpeed() => MapManager.getNowSpeed() > 0 ? 1f : 0f;
        private static void SelectedPostfix(BaseHero hero)
        {
            try { TalkRuntime.Instance?.SetSelectedHero(hero); }
            catch (Exception e) { TalkRuntime.Instance?.ReportHookError("角色选择接入", e); }
        }
        private static void MultipleSelectedPostfix(List<BaseHero> _heros)
        {
            if (_heros != null && _heros.Count > 1) TalkRuntime.Instance?.SetSelectedHero(null);
        }
        private static void MoodPostfix(FW_Attr_MoodPart __instance)
        {
            try { TalkRuntime.Instance?.OnMood(__instance); }
            catch (Exception e) { TalkRuntime.Instance?.ReportHookError("记录事件记忆", e); }
        }
        private static void RoleInfoPostfix(Dialog_MainHeroInfo __instance, FWHeroBean heroBean)
        {
            try { RoleInfoPromptButton.Attach(__instance, heroBean); }
            catch (Exception e) { TalkRuntime.Instance?.ReportUiHookError("角色信息页提示词按钮", e); }
        }
        private static bool InputPrefix() => TalkRuntime.Instance == null || !TalkRuntime.Instance.IsWindowOpen;
        private static bool KeyboardPrefix(ref bool __result)
        {
            if (InputPrefix()) return true;
            __result = true;
            return false;
        }

        private static void SnapshotPrefix(Archive __instance, out TalkRuntime.SaveCapture __state)
        {
            var runtime = TalkRuntime.Instance;
            // Saving can run on a worker: use managed null checks, not Unity's native object check.
            __state = null;
            if (ReferenceEquals(runtime, null)) return;
            try { __state = runtime.BeforeSnapshot(__instance); }
            catch (Exception e) { runtime.ReportSaveHookError("对话记忆快照失败，游戏继续保存", e); }
        }
        private static void SnapshotPostfix(object __result, TalkRuntime.SaveCapture __state)
        {
            var runtime = TalkRuntime.Instance;
            if (ReferenceEquals(runtime, null)) return;
            try { runtime.AfterSnapshot(__result, __state); }
            catch (Exception e) { runtime.ReportSaveHookError("对话记忆快照定稿失败，游戏继续保存", e); }
        }
        private static void SavePostfix(object snapshot, bool __result)
        {
            var runtime = TalkRuntime.Instance;
            if (!ReferenceEquals(runtime, null)) runtime.AfterSave(snapshot, __result);
        }
    }
}
