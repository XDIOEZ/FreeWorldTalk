using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace FreeWorldTalk
{
    public sealed class SettingsStore
    {
        private readonly string root;
        private readonly object gate = new object();
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FreeWorldTalk.ApiKey.v1");

        public SettingsStore(string root)
        {
            if (String.IsNullOrWhiteSpace(root)) throw new ArgumentException("配置目录不能为空。", "root");
            this.root = Path.GetFullPath(root);
        }

        public TalkSettings Load()
        {
            lock (gate)
            {
                string path = Path.Combine(root, "settings.json");
                TalkSettings settings;
                try
                {
                    settings = File.Exists(path)
                        ? JsonConvert.DeserializeObject<TalkSettings>(File.ReadAllText(path, Encoding.UTF8))
                        : new TalkSettings();
                }
                catch (JsonException) { throw new InvalidDataException("FreeWorldTalk 配置文件 JSON 格式无效。"); }
                if (settings == null) throw new InvalidDataException("FreeWorldTalk 配置文件是空 JSON。");
                Validate(settings);
                string secretPath = Path.Combine(root, "apikey.dpapi");
                if (File.Exists(secretPath))
                {
                    try
                    {
                        byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(secretPath), Entropy, DataProtectionScope.CurrentUser);
                        try { settings.ApiKey = Encoding.UTF8.GetString(clear); }
                        finally { Array.Clear(clear, 0, clear.Length); }
                    }
                    catch (CryptographicException)
                    {
                        throw new InvalidDataException("FreeWorldTalk API 密钥无法解密，请使用保存密钥的 Windows 用户或重新设置密钥。");
                    }
                }
                if (String.IsNullOrWhiteSpace(settings.ApiKey))
                    settings.ApiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? "";
                return settings;
            }
        }

        public void Save(TalkSettings settings)
        {
            Validate(settings);
            lock (gate)
            {
                Directory.CreateDirectory(root);
                string key = (settings.ApiKey ?? "").Trim();
                string secretPath = Path.Combine(root, "apikey.dpapi");
                if (key.Length > 0)
                {
                    byte[] clear = Encoding.UTF8.GetBytes(key);
                    try { AtomicWrite(secretPath, ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser)); }
                    finally { Array.Clear(clear, 0, clear.Length); }
                }
                else if (File.Exists(secretPath)) File.Delete(secretPath);
                // ApiKey has JsonIgnore. The regular settings file never receives credentials.
                AtomicWrite(Path.Combine(root, "settings.json"), Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(settings, Formatting.Indented)));
            }
        }

        public static void Validate(TalkSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            Uri uri;
            if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps
                || String.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("接口地址必须是无账号、查询参数和片段的 HTTPS 基础地址。");
            if (String.IsNullOrWhiteSpace(settings.Model) || settings.Model.Length > 128)
                throw new ArgumentException("模型名称不能为空且不能超过 128 字符。");
            foreach (char c in settings.Model)
                if (!Char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.' && c != '/')
                    throw new ArgumentException("模型名称含无效字符。");
            if (String.IsNullOrWhiteSpace(settings.Language) || settings.Language.Length > 128)
                throw new ArgumentException("对话语言不能为空且不能超过 128 字符。");
            if (settings.WorldDescription == null || settings.WorldDescription.Length > 8000)
                throw new ArgumentException("世界设定不能为空值或超过 8000 字符。");
            Range(settings.AutoIntervalSeconds, 10, 3600, "自动闲聊间隔");
            Range(settings.RequestTimeoutSeconds, 5, 180, "请求超时");
            Range(settings.MaxOutputTokens, 128, 4096, "输出 token 上限");
            Range(settings.RecentTurns, 1, 50, "近期对话条数");
            Range(settings.MaxMemoriesPerActor, 1, 100, "角色记忆条数");
            Range(settings.MaxCacheEntries, 1, 500, "缓存条数");
            Range(settings.CacheTtlMinutes, 1, 1440, "缓存有效分钟数");
            if ((settings.ApiKey ?? "").Length > 4096) throw new ArgumentException("API 密钥长度无效。");
            foreach (char c in settings.ApiKey ?? "")
                if (Char.IsControl(c)) throw new ArgumentException("API 密钥包含控制字符。");
        }

        private static void Range(int value, int minimum, int maximum, string label)
        {
            if (value < minimum || value > maximum)
                throw new ArgumentException(label + "应在 " + minimum + " 到 " + maximum + " 之间。");
        }

        internal static void AtomicWrite(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
