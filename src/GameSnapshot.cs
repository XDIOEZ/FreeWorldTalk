using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;

namespace FreeWorldTalk
{
    internal static class GameSnapshot
    {
        private static readonly System.Reflection.FieldInfo Tags = AccessTools.Field(typeof(FWHeroBean), "_characterTags");
        private static readonly string[] AbilityNames = { "作战", "工业", "生物", "制造", "科技", "行政", "情报" };

        internal static ActorSnapshot Actor(BaseHero hero, bool includeBackground = true)
        {
            if (hero == null || hero.heroBean == null) throw new InvalidOperationException("角色或角色数据不存在。");
            var bean = hero.heroBean;
            var text = new StringBuilder(includeBackground ? Background(bean).Profile : "");
            if (includeBackground) text.Append("；当前年龄：").Append(bean.age);
            if (bean.attr_Mood != null) text.Append("；当前心情值：").Append(bean.getMood().ToString(CultureInfo.InvariantCulture));
            if (includeBackground && bean.ability != null)
            {
                text.Append("；能力：");
                for (int i = 0; i < Math.Min(bean.ability.Length, AbilityNames.Length); i++)
                    if (bean.ability[i] != null) text.Append(AbilityNames[i]).Append('=').Append(bean.ability[i].getAttribute()).Append(' ');
            }
            if (bean.moodParts != null)
            {
                int count = 0;
                foreach (var part in bean.moodParts)
                {
                    if (part == null || !part.isRunning()) continue;
                    AppendDef(text, "当前心情原因", part.getDef());
                    if (++count >= 6) break;
                }
            }
            if (bean.diseases != null && bean.diseases.Count > 0)
                text.Append("；现有疾病数量：").Append(bean.diseases.Count);
            var action = hero.getRunAI()?.getActioning();
            if (action != null) text.Append("；当前行动类型：").Append(action.GetType().Name);
            text.Append("；俘虏：").Append(bean.prisoner ? "是" : "否");
            return new ActorSnapshot { Uid = bean.uid, Name = bean.Name, Profile = text.ToString() };
        }

        internal static ActorSnapshot Background(FWHeroBean bean, bool fullDescriptions = false)
        {
            if (bean == null) throw new ArgumentNullException(nameof(bean));
            var text = new StringBuilder();
            text.Append("姓名：").Append(bean.Name).Append("；性别：").Append(bean.sex == FWRoleBean.FEMALE ? "女" : "男");
            AppendDef(text, "种族", bean.getRaceDef(), fullDescriptions);
            AppendDef(text, "职业", bean.getOccpupationGroupDef(), fullDescriptions);
            AppendDef(text, "童年", bean.getChildGroupDef(), fullDescriptions);
            AppendDef(text, "出生地", bean.getBornDef(), fullDescriptions);
            var tags = Tags?.GetValue(bean) as List<string>;
            if (tags != null && tags.Count > 0) text.Append("；性格标签：").Append(string.Join("、", tags));
            var tones = bean.getChatPreferTones();
            if (tones != null && tones.size > 0)
            {
                text.Append("；偏好语气：");
                for (int i = 0; i < Math.Min(tones.size, 8); i++) text.Append(tones.get(i)).Append(' ');
            }
            return new ActorSnapshot { Uid = bean.uid, Name = bean.Name, Profile = text.ToString() };
        }

        // Capture Bean facts on the main thread. This also works for characters without a live BaseHero.
        internal static ActorSnapshot RolePromptProfile(FWHeroBean bean)
        {
            if (bean == null) throw new ArgumentNullException(nameof(bean));
            var text = new StringBuilder(Background(bean, true).Profile);
            text.Append("\n当前年龄：").Append(bean.getCurAge()).Append(" 岁；游戏预期寿命：")
                .Append(RoleNumber(bean.getLifeSpan())).Append(" 岁。");
            text.Append("\n能力属性（角色当前有效值）：");
            if (bean.ability == null) text.Append("未提供能力记录。");
            else
            {
                int count = 0;
                foreach (var ability in bean.ability)
                {
                    if (ability == null) continue;
                    if (ability.id < 0 || ability.id >= AbilityNames.Length)
                        throw new InvalidOperationException("角色 " + bean.uid + " 的能力 ID 无效：" + ability.id);
                    text.Append(AbilityNames[ability.id]).Append(' ').Append(ability.getAttribute(bean))
                        .Append("（等级上限 ").Append(bean.getAbilityLvLimit(ability.id)).Append("）；");
                    count++;
                }
                if (count == 0) text.Append("未提供有效能力记录。");
            }
            text.Append("\n工作效率：").Append(RoleNumber(FormulaHander.get_全局工作效率(bean) * 100)).Append("%；")
                .Append("作战效率：").Append(RoleNumber(FormulaHander.getHeroInfo_PowerAtk(bean))).Append("；")
                .Append("耐温范围：").Append(bean.getLowTemplerLimit()).Append("～").Append(bean.getHotTemplerLimit()).Append("℃。");
            text.Append("\n当前身体状态（生成时的记录，会随游戏变化）：死亡=").Append(bean.isDead() ? "是" : "否");
            if (bean.attr_Hp != null)
            {
                text.Append("；倒地=").Append(bean.isFallDown() ? "是" : "否")
                    .Append("；意识/生命值=").Append(RoleNumber(bean.attr_Hp.hp)).Append('/').Append(bean.getMaxHp())
                    .Append("；临时肾上腺生命值=").Append(RoleNumber(bean.attr_Hp.tempHP));
            }
            else text.Append("；意识/生命值记录未提供");
            text.Append("；休克=").Append(bean.isBuffing_休克() ? "是" : "否")
                .Append("；麻醉=").Append(bean.isBuffing_麻醉() ? "是" : "否")
                .Append("；缺氧=").Append(bean.attr_Gas < 0 ? "是" : "否");
            if (bean.attr_Mood != null) text.Append("；当前心情值=").Append(RoleNumber(bean.getMood()));
            text.Append("；俘虏=").Append(bean.prisoner ? "是" : "否").Append('。');

            AppendRoleDiseases(text, bean);
            AppendRoleBody(text, bean);
            return new ActorSnapshot { Uid = bean.uid, Name = bean.Name, Profile = text.ToString() };
        }

        private static string RoleNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new InvalidOperationException("角色资料包含无效数值，不能生成角色提示词。");
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static void AppendRoleDiseases(StringBuilder text, FWHeroBean bean)
        {
            text.Append("\n疾病记录：");
            if (bean.diseases == null) { text.Append("未提供。"); return; }
            if (bean.diseases.Count == 0) { text.Append("当前记录中无疾病。"); return; }
            const int limit = 12;
            for (int i = 0; i < Math.Min(bean.diseases.Count, limit); i++)
            {
                var disease = bean.diseases[i];
                var def = disease?.getDef();
                if (def == null)
                    throw new InvalidOperationException("角色 " + bean.uid + " 的疾病记录 " + i + " 缺少配置。");
                text.Append(def.QCName).Append("（病情进度 ").Append(RoleNumber(disease.getDiseaseProgress() * 100))
                    .Append("%，免疫值 ").Append(RoleNumber(disease.immunity)).Append("）；");
            }
            if (bean.diseases.Count > limit) text.Append("另有 ").Append(bean.diseases.Count - limit).Append(" 项疾病记录省略。");
        }

        private static void AppendRoleBody(StringBuilder text, FWHeroBean bean)
        {
            text.Append("\n身体部件：");
            if (bean.parts == null) { text.Append("未提供记录。"); return; }
            var damaged = new List<string>();
            var intact = new List<string>();
            foreach (var part in bean.parts)
            {
                var organ = part?.getDef();
                if (organ == null)
                    throw new InvalidOperationException("角色 " + bean.uid + " 的身体部件记录缺少配置。");
                if (organ.OrganType == -1) continue; // Same organ filter as the game's health page.
                var entry = new StringBuilder(organ.QCName);
                bool loss = part.isLoss();
                if (part.partThing == null) entry.Append("：缺失");
                else
                {
                    var def = part.getPartDef();
                    if (def == null)
                        throw new InvalidOperationException("角色 " + bean.uid + " 的身体部件 " + part.partID + " 缺少物品配置。");
                    entry.Append("（").Append(def.QCName).Append("）：HP ").Append(RoleNumber(part.partThing.hp))
                        .Append('/').Append(RoleNumber(def.HP)).Append(loss ? "，已损毁" : part.isHurt() ? "，受伤" : "，完好");
                }
                if (part.hasOpera()) entry.Append("，已有手术计划");
                (loss || part.isHurt() || part.hasOpera() ? damaged : intact).Add(entry.ToString());
            }
            // Keep damaged/missing organs visible before healthy organs when records are numerous.
            damaged.AddRange(intact);
            const int limit = 24;
            for (int i = 0; i < Math.Min(damaged.Count, limit); i++) text.Append(damaged[i]).Append('；');
            if (damaged.Count == 0) text.Append("没有可用的器官记录。");
            if (damaged.Count > limit) text.Append("另有 ").Append(damaged.Count - limit).Append(" 项身体部件记录省略。");

            text.Append("\n身体机能（游戏当前有效值）：");
            var senseIds = new[] { RoleSensoryDef.思维, RoleSensoryDef.视觉, RoleSensoryDef.反应, RoleSensoryDef.呼吸控制,
                RoleSensoryDef.操作能力, RoleSensoryDef.体液平衡, RoleSensoryDef.血液循环, RoleSensoryDef.新陈代谢, RoleSensoryDef.移动能力 };
            var senseNames = new[] { "心理素质", "视觉", "听觉/反应", "呼吸", "操作", "体液平衡", "血液循环", "新陈代谢", "移动能力" };
            for (int i = 0; i < senseIds.Length; i++)
                text.Append(senseNames[i]).Append(' ').Append(RoleNumber(bean.getSenseEff(senseIds[i]) * 100)).Append("%；");
        }

        private static void AppendDef(StringBuilder text, string label, BaseDef def, bool fullDescription = false)
        {
            if (def == null) return; // Optional background data is not invented.
            text.Append("；").Append(label).Append('：').Append(def.getName());
            string desc = def.getDesc();
            if (!string.IsNullOrWhiteSpace(desc) && desc != "undefined" && desc != "-1")
                text.Append('（').Append(!fullDescription && desc.Length > 180 ? desc.Substring(0, 180) : desc).Append('）');
        }

        internal static string Map()
        {
            var user = UserBean.tryGetInstance();
            var map = user?.curStarMap?.starMapInfo;
            if (user?.gameData == null || map == null) throw new InvalidOperationException("当前没有已加载的地面地图。");
            return "地图：" + map.Name + "；地图UID：" + map.starMapId + "；时间：" +
                WorldTimeTools.mm_GameDHM(user.gameData.elapsedMM);
        }

        internal static string PromptWorldKey(ArchiveInfo info)
        {
            if (info == null) throw new InvalidOperationException("角色提示词缺少存档元信息。");
            if (!string.IsNullOrWhiteSpace(info.marketHistoryId)) return "history:" + info.marketHistoryId;
            var name = info.GetSourceManualArchiveName();
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("角色提示词缺少世界或存档身份。");
            return "legacy:" + name;
        }

        internal static string CheckpointKey(ArchiveInfo info)
        {
            if (info == null) throw new InvalidOperationException("存档元信息不存在。");
            return (info.marketHistoryId ?? "") + "|" + (info.name ?? "") + "|" + info.saveTime.ToString(CultureInfo.InvariantCulture);
        }

        internal static bool CanTalk(BaseHero hero, out string reason)
        {
            var user = UserBean.tryGetInstance();
            if (user?.gameData == null || user.curStarMap == null || !MapManager.isInitFinish || MapManager.getInstance() == null || RootUtils.isSwitchSceneing)
            { reason = "地面场景尚未就绪"; return false; }
            if (hero == null || hero.heroBean == null || hero.heroBean.isDead())
            { reason = "角色已离开或死亡"; return false; }
            if (hero.heroBean.starMapId != user.curStarMap.starMapInfo.starMapId)
            { reason = "角色已离开当前地图"; return false; }
            if (hero.attrDriven == null || hero.getRunAI() == null)
            { reason = "角色行为系统尚未就绪"; return false; }
            if (!hero.canCommand() || hero.attrDriven.isAction_InSleep() || hero.isAIActive(AI_Base.AI_TYPE.战斗))
            { reason = "角色正在睡眠、战斗或无法交流"; return false; }
            if (hero.getRunAI().isInSocialing() || (hero.getRunAI().getActioning()?.isGrave() ?? false))
            { reason = "角色正在其他社交或重要行动中"; return false; }
            reason = "";
            return true;
        }
    }
}
