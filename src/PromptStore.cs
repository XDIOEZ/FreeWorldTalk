using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FreeWorldTalk
{
    // Prompt text is authored in JSON. No embedded/default prompt is used on read failure.
    public sealed class PromptStore
    {
        public const int MaxRoleCharacters = 6000;
        public const int MaxCommonRoleCharacters = 32000;
        private readonly object gate = new object();
        private readonly string rolesRoot;
        private readonly string migrationPath;
        public string GlobalPath { get; }
        public string RoleGeneratorPath { get; }

        public PromptStore(string dataRoot, string modRoot)
        {
            rolesRoot = Path.Combine(Path.GetFullPath(dataRoot), "roles");
            GlobalPath = Path.Combine(Path.GetFullPath(modRoot), "Prompts", "prompts.json");
            migrationPath = Path.Combine(Path.GetFullPath(modRoot), "Prompts", "migration-v1.json");
            RoleGeneratorPath = Path.Combine(Path.GetFullPath(modRoot), "Prompts", "role-generator.json");
        }

        public RoleGeneratorConfig LoadRoleGenerator()
        {
            var config = Read<RoleGeneratorConfig>(RoleGeneratorPath);
            if (config == null || config.Version != 1) throw new InvalidDataException("角色提示词生成配置版本无效：" + RoleGeneratorPath);
            Required(config.SystemPrompt, 32000, "角色生成 SystemPrompt");
            Required(config.RequestInstruction, 8000, "角色生成 RequestInstruction");
            if (config.MaxOutputTokens < 128 || config.MaxOutputTokens > 4096)
                throw new InvalidDataException("角色生成 MaxOutputTokens 应在 128 到 4096 之间。");
            return config;
        }

        public PromptConfig LoadGlobal()
        {
            lock (gate)
            {
                var document = Read<JObject>(GlobalPath);
                if (document == null) throw new InvalidDataException("提示词 JSON 为空：" + GlobalPath);
                var rules = ReadMigration();
                bool changed = false;
                if (document.Value<int?>("Version") == 1)
                {
                    MigrateGlobal(document, rules);
                    changed = true;
                }
                if (document.Value<int?>("Version") == 2 && document["RolePromptTemplate"]?.Type == JTokenType.String
                    && string.Equals(document.Value<string>("RolePromptTemplate"), rules.LegacyProfileTemplate, StringComparison.Ordinal))
                {
                    document["RolePromptTemplate"] = rules.IdentityRoleTemplate;
                    changed = true;
                }
                var config = ValidateGlobal(document);
                if (changed) BackupAndWrite(GlobalPath, document);
                return config;
            }
        }

        public string GetCommonRole() => LoadGlobal().CommonRolePrompt;

        public void SaveCommonRole(string text)
        {
            ValidateCommon(text);
            lock (gate)
            {
                LoadGlobal();
                // Edit only this property: player-authored and unknown JSON fields are retained.
                var document = Read<JObject>(GlobalPath);
                document["CommonRolePrompt"] = text;
                ValidateGlobal(document);
                WriteDocument(GlobalPath, document);
            }
        }

        private PromptConfig ValidateGlobal(JObject document)
        {
            var config = document.ToObject<PromptConfig>();
            if (config == null || config.Version != 2) throw new InvalidDataException("提示词 JSON 版本无效：" + GlobalPath);
            Required(config.GlobalSystemPrompt, 32000, "GlobalSystemPrompt");
            ValidateCommon(config.CommonRolePrompt);
            Required(config.PlayerModePrompt, 16000, "PlayerModePrompt");
            Required(config.SocialModePrompt, 16000, "SocialModePrompt");
            Required(config.SpeakerRequestContract, 8000, "SpeakerRequestContract");
            Required(config.RolePromptTemplate, MaxRoleCharacters, "RolePromptTemplate");
            Required(config.RequestInstruction, 8000, "RequestInstruction");
            // Validate template names before any employee files are created.
            Expand(config.GlobalSystemPrompt, new Dictionary<string, string> {
                ["WorldDescription"] = "", ["Language"] = "", ["MaxLineCharacters"] = "" });
            Expand(config.RolePromptTemplate, new Dictionary<string, string> {
                ["Name"] = "", ["Uid"] = "", ["Profile"] = "" });
            return config;
        }

        // Explicit upgrade entry point for installation maintenance; runtime also upgrades on read.
        public int MigrateLegacyRoles()
        {
            lock (gate)
            {
                if (!Directory.Exists(rolesRoot)) return 0;
                int changed = 0;
                foreach (string directory in Directory.GetDirectories(rolesRoot))
                {
                    string scope = Path.GetFileName(directory);
                    if (!Regex.IsMatch(scope, "^[0-9a-f]{64}$")) continue;
                    DirectoryFor(scope);
                    foreach (string path in Directory.GetFiles(directory, "*.json"))
                    {
                        if (!long.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None, CultureInfo.InvariantCulture, out long uid)
                            || uid <= 0) throw new InvalidDataException("角色提示词文件名不是有效 UID：" + path);
                        bool legacy = Read<JObject>(path)?.Value<int?>("Version") == 1;
                        ReadRole(path, scope, uid);
                        if (legacy) changed++;
                    }
                }
                return changed;
            }
        }

        public string BindWorld(string worldKey)
        {
            Required(worldKey, 4096, "世界标识");
            lock (gate)
            {
                var index = ReadWorldIndex();
                if (index.Aliases.TryGetValue(worldKey, out string scope)) return scope;
                scope = Hash(worldKey);
                index.Aliases.Add(worldKey, scope);
                WriteWorldIndex(index);
                return scope;
            }
        }

        public void AddWorldAlias(string worldKey, string scope)
        {
            Required(worldKey, 4096, "世界标识");
            DirectoryFor(scope);
            lock (gate)
            {
                var index = ReadWorldIndex();
                if (index.Aliases.TryGetValue(worldKey, out string existing))
                {
                    if (existing != scope) throw new InvalidDataException("该存档已有另一组角色提示词，不能自动覆盖身份映射。");
                    return;
                }
                index.Aliases.Add(worldKey, scope);
                WriteWorldIndex(index);
            }
        }

        public string DirectoryFor(string scope)
        {
            if (scope == null || scope.Length != 64 || scope.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))))
                throw new ArgumentException("角色提示词世界标识无效。");
            return Path.Combine(rolesRoot, scope);
        }

        public string EnsureRole(string scope, ActorSnapshot actor, PromptConfig config)
        {
            if (actor == null) throw new ArgumentNullException(nameof(actor));
            string path = RolePath(scope, actor.Uid);
            lock (gate)
            {
                var values = new Dictionary<string, string> {
                    ["Name"] = actor.Name, ["Uid"] = actor.Uid.ToString(CultureInfo.InvariantCulture), ["Profile"] = actor.Profile };
                if (File.Exists(path))
                {
                    var role = ReadRole(path, scope, actor.Uid);
                    if (role.Version == 2)
                    {
                        var rules = ReadMigration();
                        // Only a byte-for-byte generated background may be removed automatically.
                        // Customized text is promoted unchanged so later edits are never reinterpreted.
                        if (string.Equals(role.Prompt, Expand(rules.LegacyProfileTemplate, values), StringComparison.Ordinal))
                        {
                            role.ImportedBackground = actor.Profile;
                            role.Prompt = Expand(rules.IdentityRoleTemplate, values);
                            Required(role.Prompt, MaxRoleCharacters, "迁移后的角色提示词");
                        }
                        role.Version = 3;
                        var document = JObject.FromObject(role);
                        BackupAndWrite(path, document);
                    }
                    return role.Prompt;
                }
                string prompt = Expand(config.RolePromptTemplate, values);
                Required(prompt, MaxRoleCharacters, "角色提示词");
                WriteRole(path, new RolePrompt { Version = 3, World = scope, Uid = actor.Uid, Name = actor.Name, Prompt = prompt });
                return prompt;
            }
        }

        public void SaveRole(string scope, long uid, string prompt)
        {
            Required(prompt, MaxRoleCharacters, "角色提示词");
            lock (gate)
            {
                string path = RolePath(scope, uid);
                var role = ReadRole(path, scope, uid);
                role.Prompt = prompt;
                role.Version = 3;
                WriteRole(path, role);
            }
        }

        public void PrepareRequest(DialogueRequest request, TalkSettings settings, string scope, PromptConfig config)
        {
            if (request == null || request.Actors == null || request.Actors.Count == 0)
                throw new ArgumentException("提示词请求缺少角色资料。");
            ActorSnapshot speaker;
            if (request.Mode == "player")
            {
                speaker = request.Actors[0];
                if (speaker == null || request.SpeakerUid != speaker.Uid) throw new ArgumentException("玩家对话的发言 UID 与目标角色不符。");
            }
            else if (request.Mode == "social")
            {
                var matches = request.Actors.Where(actor => actor != null && actor.Uid == request.SpeakerUid).ToArray();
                if (matches.Length != 1) throw new ArgumentException("闲聊发言 UID 必须对应唯一角色。");
                speaker = matches[0];
                if (request.SeedLines == null || !request.SeedLines.Any(line => line != null && line.SpeakerUid == speaker.Uid))
                    throw new ArgumentException("闲聊发言 UID 不在原始台词中。");
            }
            else throw new ArgumentException("未知提示词对话模式。");
            var roles = new[] { new { speaker.Uid, speaker.Name, Prompt = EnsureRole(scope, speaker, config) } };
            string global = Expand(config.GlobalSystemPrompt, new Dictionary<string, string> {
                ["WorldDescription"] = settings.WorldDescription, ["Language"] = settings.Language,
                ["MaxLineCharacters"] = DeepSeekClient.MaxLineCharacters.ToString(CultureInfo.InvariantCulture) });
            string mode;
            if (request.Mode == "player") mode = config.PlayerModePrompt;
            else if (request.Mode == "social") mode = config.SocialModePrompt;
            else throw new ArgumentException("未知提示词对话模式。");
            request.SystemPrompt = global + "\n\n" + config.CommonRolePrompt + "\n\n" + mode + "\n\n"
                + JsonConvert.SerializeObject(new { SpeakerUid = speaker.Uid, RolePrompts = roles }) + "\n\n" + config.SpeakerRequestContract;
            request.RequestInstruction = config.RequestInstruction;
        }

        private string RolePath(string scope, long uid) => Path.Combine(DirectoryFor(scope), uid.ToString(CultureInfo.InvariantCulture) + ".json");
        private RolePrompt ReadRole(string path, string scope, long uid)
        {
            var document = Read<JObject>(path);
            var role = document?.ToObject<RolePrompt>();
            if (role == null || (role.Version != 1 && role.Version != 2 && role.Version != 3) || role.World != scope || role.Uid != uid)
                throw new InvalidDataException("角色提示词的版本、世界或 UID 与文件不符：" + path);
            Required(role.Name, 128, "角色名称");
            Required(role.Prompt, MaxRoleCharacters, "角色提示词");
            if (role.Version == 1)
            {
                role.Prompt = RemoveKnownTail(role.Prompt, ReadMigration());
                Required(role.Prompt, MaxRoleCharacters, "迁移后的角色提示词");
                role.Version = 2;
                document["Version"] = role.Version;
                document["Prompt"] = role.Prompt;
                BackupAndWrite(path, document);
            }
            return role;
        }
        private static void WriteRole(string path, RolePrompt role) => SettingsStore.AtomicWrite(path,
            Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(role, Formatting.Indented)));

        private MigrationRules ReadMigration()
        {
            var rules = Read<MigrationRules>(migrationPath);
            if (rules == null || rules.Version != 1 || rules.SourceVersion != 1 || rules.TargetVersion != 2
                || rules.GlobalReplacements == null)
                throw new InvalidDataException("提示词迁移规则版本无效：" + migrationPath);
            ValidateCommon(rules.CommonRolePrompt);
            Required(rules.RolePromptSuffix, MaxRoleCharacters, "RolePromptSuffix");
            if (!rules.RolePromptSuffix.StartsWith("\n", StringComparison.Ordinal))
                throw new InvalidDataException("角色提示词迁移尾段必须包含原有换行分隔。");
            Required(rules.SpeakerRequestContract, 8000, "SpeakerRequestContract");
            Required(rules.LegacyProfileTemplate, MaxRoleCharacters, "LegacyProfileTemplate");
            Required(rules.IdentityRoleTemplate, MaxRoleCharacters, "IdentityRoleTemplate");
            Expand(rules.LegacyProfileTemplate, new Dictionary<string, string> {
                ["Name"] = "", ["Uid"] = "", ["Profile"] = "" });
            Expand(rules.IdentityRoleTemplate, new Dictionary<string, string> { ["Name"] = "", ["Uid"] = "" });
            string[] allowed = { "GlobalSystemPrompt", "PlayerModePrompt", "SocialModePrompt", "RequestInstruction" };
            foreach (var entry in rules.GlobalReplacements)
            {
                if (!allowed.Contains(entry.Key) || entry.Value == null)
                    throw new InvalidDataException("提示词迁移规则包含未知字段：" + entry.Key);
                Required(entry.Value.Before, 32000, entry.Key + ".Before");
                Required(entry.Value.After, 32000, entry.Key + ".After");
            }
            return rules;
        }

        private static void MigrateGlobal(JObject document, MigrationRules rules)
        {
            // Exact legacy matches only. A customized protocol field remains player-authored.
            foreach (var entry in rules.GlobalReplacements)
                if (document[entry.Key]?.Type == JTokenType.String
                    && string.Equals(document.Value<string>(entry.Key), entry.Value.Before, StringComparison.Ordinal))
                    document[entry.Key] = entry.Value.After;
            if (document.Property("CommonRolePrompt") == null) document["CommonRolePrompt"] = rules.CommonRolePrompt;
            if (document.Property("SpeakerRequestContract") == null) document["SpeakerRequestContract"] = rules.SpeakerRequestContract;
            if (document["RolePromptTemplate"]?.Type != JTokenType.String)
                throw new InvalidDataException("RolePromptTemplate 必须为字符串。");
            document["RolePromptTemplate"] = RemoveKnownTail(document.Value<string>("RolePromptTemplate"), rules);
            document["Version"] = rules.TargetVersion;
        }

        private static string RemoveKnownTail(string text, MigrationRules rules) =>
            text.EndsWith(rules.RolePromptSuffix, StringComparison.Ordinal)
                ? text.Substring(0, text.Length - rules.RolePromptSuffix.Length) : text;

        private static void BackupAndWrite(string path, JObject document)
        {
            string backup = path + ".before-common-role." + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture)
                + "." + Guid.NewGuid().ToString("N") + ".bak";
            File.Copy(path, backup, false);
            WriteDocument(path, document);
        }

        private static void WriteDocument(string path, JObject document) => SettingsStore.AtomicWrite(path,
            Encoding.UTF8.GetBytes(document.ToString(Formatting.Indented)));

        private WorldIndex ReadWorldIndex()
        {
            string path = Path.Combine(rolesRoot, "worlds.json");
            var index = File.Exists(path) ? Read<WorldIndex>(path) : new WorldIndex { Version = 1 };
            if (index == null || index.Version != 1 || index.Aliases == null)
                throw new InvalidDataException("角色提示词世界索引无效：" + path);
            foreach (var entry in index.Aliases) { Required(entry.Key, 4096, "世界标识"); DirectoryFor(entry.Value); }
            return index;
        }
        private void WriteWorldIndex(WorldIndex index) => SettingsStore.AtomicWrite(Path.Combine(rolesRoot, "worlds.json"),
            Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(index, Formatting.Indented)));
        private static T Read<T>(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("提示词 JSON 文件不存在。", path);
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("提示词 JSON 文件超过 1MB：" + path);
            try { return JsonConvert.DeserializeObject<T>(File.ReadAllText(path, Encoding.UTF8)); }
            catch (JsonException e) { throw new InvalidDataException("提示词 JSON 无效：" + path + "；" + e.Message, e); }
        }
        private static void Required(string value, int max, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new InvalidDataException(name + "不能为空或超过 " + max + " 字符。");
        }
        private static void ValidateCommon(string value)
        {
            // A player may deliberately clear the shared role guidance; the JSON property must still exist.
            if (value == null || value.Length > MaxCommonRoleCharacters)
                throw new InvalidDataException("通用角色提示词不能为 null 或超过 " + MaxCommonRoleCharacters + " 字符。");
        }
        private static string Expand(string template, Dictionary<string, string> values) => Regex.Replace(template,
            @"\{\{([A-Za-z]+)\}\}", match => values.TryGetValue(match.Groups[1].Value, out string value) ? value ?? "" :
                throw new InvalidDataException("提示词模板包含未知占位符：" + match.Value));
        private static string Hash(string text)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        public sealed class RoleGeneratorConfig
        {
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public int Version { get; set; }
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public string SystemPrompt { get; set; }
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public string RequestInstruction { get; set; }
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public int MaxOutputTokens { get; set; }
        }

        public sealed class PromptConfig
        {
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public int Version;
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public string GlobalSystemPrompt;
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public string CommonRolePrompt;
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public string PlayerModePrompt;
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public string SocialModePrompt;
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public string RolePromptTemplate;
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public string RequestInstruction;
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public string SpeakerRequestContract;
        }
        private sealed class RolePrompt
        {
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public int Version;
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public string World;
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public long Uid;
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public string Name;
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public string Prompt;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string ImportedBackground;
            [JsonExtensionData] public IDictionary<string, JToken> ExtraFields;
        }
        private sealed class MigrationRules
        {
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public int Version;
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public int SourceVersion;
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public int TargetVersion;
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public string CommonRolePrompt;
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public string RolePromptSuffix;
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public string SpeakerRequestContract;
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public string LegacyProfileTemplate;
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public string IdentityRoleTemplate;
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public Dictionary<string, PromptReplacement> GlobalReplacements;
        }
        private sealed class PromptReplacement
        {
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public string Before;
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public string After;
        }
        private sealed class WorldIndex
        {
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public int Version;
            [JsonProperty(Required = Newtonsoft.Json.Required.Always)] public Dictionary<string, string> Aliases = new Dictionary<string, string>();
        }
    }
}
