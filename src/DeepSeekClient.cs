using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FreeWorldTalk
{
    public sealed class DeepSeekClient
    {
        public const int MaxLineCharacters = 600;
        public const int MaxPlayerCharacters = 2000;
        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            client.Timeout = Timeout.InfiniteTimeSpan;
            return client;
        }

        public async Task<DialogueResponse> GenerateAsync(DialogueRequest request, TalkSettings settings, CancellationToken cancellationToken)
        {
            SettingsStore.Validate(settings);
            ValidateRequest(request);
            string json = await SendAsync(request.SystemPrompt, request.RequestInstruction, request, settings, cancellationToken).ConfigureAwait(false);
            return Parse(json, request);
        }

        public async Task<RolePromptResponse> GenerateRolePromptAsync(RolePromptRequest request, TalkSettings settings, CancellationToken cancellationToken)
        {
            SettingsStore.Validate(settings);
            ValidateRolePromptRequest(request);
            string json = await SendAsync(request.SystemPrompt, request.RequestInstruction, request, settings, cancellationToken).ConfigureAwait(false);
            return ParseRolePrompt(json);
        }

        private static async Task<string> SendAsync(string systemPrompt, string requestInstruction, object request, TalkSettings settings, CancellationToken cancellationToken)
        {
            if (String.IsNullOrWhiteSpace(settings.ApiKey))
                throw new InvalidOperationException("尚未配置 DeepSeek API 密钥，请在设置中填写或设置 DEEPSEEK_API_KEY 环境变量。");
            string body = JsonConvert.SerializeObject(new
            {
                model = settings.Model,
                messages = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = requestInstruction + "\n" + JsonConvert.SerializeObject(request) } },
                response_format = new { type = "json_object" },
                max_tokens = settings.MaxOutputTokens,
                temperature = 0.8,
                stream = false
            });
            Uri endpoint = new Uri(settings.Endpoint.TrimEnd('/') + "/chat/completions");
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (deadline.IsCancellationRequested) throw new TimeoutException("DeepSeek 请求超过配置的超时时间。");
                    try
                    {
                        using (var message = new HttpRequestMessage(HttpMethod.Post, endpoint))
                        {
                            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
                            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
                            using (var response = await Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                            {
                                int code = (int)response.StatusCode;
                                if (!response.IsSuccessStatusCode)
                                {
                                    // Do not expose provider response bodies: they can echo input or credentials.
                                    if (attempt == 0 && (code == 429 || code == 408 || code >= 500))
                                    {
                                        await Task.Delay(1000, deadline.Token).ConfigureAwait(false);
                                        continue;
                                    }
                                    throw new InvalidOperationException("DeepSeek 请求失败，HTTP " + code + ". " + StatusHint(code));
                                }
                                return await ReadBody(response.Content, deadline.Token).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (HttpRequestException)
                    {
                        if (attempt == 1) throw new InvalidOperationException("DeepSeek 网络连接失败，请检查连接及接口地址。");
                        try { await Task.Delay(1000, deadline.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException)
                        {
                            if (cancellationToken.IsCancellationRequested) throw;
                            throw new TimeoutException("DeepSeek 请求超过配置的超时时间。");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (cancellationToken.IsCancellationRequested) throw;
                        throw new TimeoutException("DeepSeek 请求超过配置的超时时间。");
                    }
                }
            }
            throw new InvalidOperationException("DeepSeek 请求未产生有效结果。");
        }

        private static async Task<string> ReadBody(HttpContent content, CancellationToken token)
        {
            if (content.Headers.ContentLength.HasValue && content.Headers.ContentLength.Value > 1024 * 1024)
                throw new InvalidDataException("DeepSeek 返回内容超过安全长度限制。");
            using (Stream stream = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var output = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                {
                    if (output.Length + count > 1024 * 1024) throw new InvalidDataException("DeepSeek 返回内容超过安全长度限制。");
                    output.Write(buffer, 0, count);
                }
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }

        private static DialogueResponse Parse(string json, DialogueRequest request)
        {
            try
            {
                JObject envelope;
                JObject payload = ParsePayload(json, out envelope);
                JArray rawLines = payload["lines"] as JArray;
                if (rawLines == null) throw new InvalidDataException("DeepSeek 回复缺少 lines 数组。");
                var result = new DialogueResponse();
                foreach (JToken line in rawLines)
                {
                    if (line.Type != JTokenType.Object || line["speakerUid"] == null || line["speakerUid"].Type != JTokenType.Integer
                        || line["text"] == null || line["text"].Type != JTokenType.String)
                        throw new InvalidDataException("DeepSeek 回复台词结构无效。");
                    result.Lines.Add(new TalkLine { SpeakerUid = (long)line["speakerUid"], Text = ((string)line["text"]).Trim() });
                }
                var usage = envelope["usage"] as JObject;
                if (usage != null)
                {
                    result.PromptTokens = ReadTokenCount(usage["prompt_tokens"]);
                    result.CompletionTokens = ReadTokenCount(usage["completion_tokens"]);
                }
                ValidateResponse(request, result);
                return result;
            }
            catch (JsonException) { throw new InvalidDataException("DeepSeek 返回无效 JSON 对话结构。"); }
            catch (OverflowException) { throw new InvalidDataException("DeepSeek 返回超出范围的数值。"); }
            catch (FormatException) { throw new InvalidDataException("DeepSeek 返回无效对话数值。"); }
        }

        private static JObject ParsePayload(string json, out JObject envelope)
        {
            envelope = JObject.Parse(json);
            JObject choice = envelope["choices"] != null && envelope["choices"].Type == JTokenType.Array
                ? envelope["choices"].First as JObject : null;
            if (choice == null || choice["finish_reason"] == null || choice["finish_reason"].Type != JTokenType.String
                || (string)choice["finish_reason"] != "stop")
                throw new InvalidDataException("DeepSeek 回复未完整结束，拒绝使用截断或非文本结果。");
            JObject message = choice["message"] as JObject;
            if (message == null || message["content"] == null || message["content"].Type != JTokenType.String)
                throw new InvalidDataException("DeepSeek 回复缺少文本内容字段。");
            string text = (string)message["content"];
            if (String.IsNullOrWhiteSpace(text)) throw new InvalidDataException("DeepSeek 返回空回复。");
            return JObject.Parse(text);
        }

        private static RolePromptResponse ParseRolePrompt(string json)
        {
            try
            {
                JObject envelope;
                JObject payload = ParsePayload(json, out envelope);
                if (payload["prompt"] == null || payload["prompt"].Type != JTokenType.String)
                    throw new InvalidDataException("DeepSeek 回复缺少角色提示词 prompt 字符串。");
                var result = new RolePromptResponse { Prompt = ((string)payload["prompt"]).Trim() };
                if (String.IsNullOrWhiteSpace(result.Prompt) || result.Prompt.Length > 6000
                    || result.Prompt.IndexOf('<') >= 0 || result.Prompt.IndexOf('>') >= 0)
                    throw new InvalidDataException("DeepSeek 生成的角色提示词为空、超过 6000 字符或含富文本标记。");
                foreach (char c in result.Prompt)
                    if (Char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')
                        throw new InvalidDataException("DeepSeek 生成的角色提示词包含无效控制字符。");
                var usage = envelope["usage"] as JObject;
                if (usage != null)
                {
                    result.PromptTokens = ReadTokenCount(usage["prompt_tokens"]);
                    result.CompletionTokens = ReadTokenCount(usage["completion_tokens"]);
                }
                if ((long)result.PromptTokens + result.CompletionTokens > Int32.MaxValue)
                    throw new InvalidDataException("角色提示词生成 token 用量字段无效。");
                return result;
            }
            catch (JsonException) { throw new InvalidDataException("DeepSeek 返回无效 JSON 角色提示词结构。"); }
            catch (OverflowException) { throw new InvalidDataException("DeepSeek 返回超出范围的数值。"); }
            catch (FormatException) { throw new InvalidDataException("DeepSeek 返回无效角色提示词数值。"); }
        }

        internal static void ValidateRolePromptRequest(RolePromptRequest request)
        {
            if (request == null || String.IsNullOrWhiteSpace(request.SystemPrompt) || String.IsNullOrWhiteSpace(request.RequestInstruction))
                throw new ArgumentException("角色提示词生成缺少从 JSON 载入的指令。");
            if (request.SystemPrompt.Length > 128000 || request.RequestInstruction.Length > 8000)
                throw new ArgumentException("角色提示词生成指令超过长度限制。");
            if (request.Actor == null || request.Actor.Uid <= 0 || String.IsNullOrWhiteSpace(request.Actor.Name)
                || String.IsNullOrWhiteSpace(request.Actor.Profile))
                throw new ArgumentException("角色提示词生成缺少有效角色身份或资料。");
            if (JsonConvert.SerializeObject(request).Length > 100000)
                throw new ArgumentException("角色提示词生成资料超过长度限制。");
        }

        private static int ReadTokenCount(JToken value)
        {
            if (value == null) return 0;
            if (value.Type != JTokenType.Integer || (long)value < 0 || (long)value > Int32.MaxValue)
                throw new InvalidDataException("DeepSeek token 用量字段无效。");
            return (int)value;
        }

        internal static void ValidateRequest(DialogueRequest request)
        {
            if (request == null || String.IsNullOrWhiteSpace(request.SystemPrompt) || String.IsNullOrWhiteSpace(request.RequestInstruction))
                throw new ArgumentException("对话缺少从 JSON 载入的提示词。");
            if (request.SystemPrompt.Length > 128000 || request.RequestInstruction.Length > 8000)
                throw new ArgumentException("对话提示词超过长度限制。");
            if (request == null || request.Actors == null || request.Actors.Count == 0 || request.Actors.Count > 8)
                throw new ArgumentException("对话缺少角色或角色数量无效。");
            var ids = new HashSet<long>();
            foreach (ActorSnapshot actor in request.Actors)
                if (actor == null || !ids.Add(actor.Uid) || String.IsNullOrWhiteSpace(actor.Name))
                    throw new ArgumentException("对话角色缺少名称或 UID 重复。");
            if (!ids.Contains(request.SpeakerUid)) throw new ArgumentException("发言目标不属于本次对话角色。");
            if (request.Mode == "social")
            {
                if (request.SeedLines == null || request.SeedLines.Count < 1 || request.SeedLines.Count > 32)
                    throw new ArgumentException("闲聊原始台词数量无效。");
                foreach (TalkLine line in request.SeedLines)
                    if (line == null || !ids.Contains(line.SpeakerUid) || String.IsNullOrWhiteSpace(line.Text))
                        throw new ArgumentException("闲聊原始台词的发言者或文本无效。");
                if (!request.SeedLines.Any(line => line.SpeakerUid == request.SpeakerUid))
                    throw new ArgumentException("闲聊发言目标没有对应的原始台词。");
            }
            else if (request.Mode == "player")
            {
                if (request.Actors.Count != 1 || request.SpeakerUid != request.Actors[0].Uid)
                    throw new ArgumentException("玩家对话只能指定当前角色作为发言目标。");
                if (String.IsNullOrWhiteSpace(request.PlayerText) || request.PlayerText.Length > MaxPlayerCharacters)
                    throw new ArgumentException("玩家输入不能为空或超过 " + MaxPlayerCharacters + " 字符。");
            }
            else throw new ArgumentException("未知对话类型。");
            if (JsonConvert.SerializeObject(request).Length > 100000) throw new ArgumentException("对话上下文超过长度限制。");
        }

        internal static void ValidateResponse(DialogueRequest request, DialogueResponse response)
        {
            if (response == null || response.Lines == null) throw new InvalidDataException("对话结果为空。");
            if (response.PromptTokens < 0 || response.CompletionTokens < 0 || (long)response.PromptTokens + response.CompletionTokens > Int32.MaxValue)
                throw new InvalidDataException("对话 token 用量字段无效。");
            int expected = request.Mode == "social" ? request.SeedLines.Count(line => line.SpeakerUid == request.SpeakerUid) : 1;
            if (response.Lines.Count != expected) throw new InvalidDataException("DeepSeek 回复台词条数与请求不符。");
            for (int i = 0; i < expected; i++)
            {
                TalkLine line = response.Lines[i];
                long uid = request.SpeakerUid;
                if (line == null || line.SpeakerUid != uid) throw new InvalidDataException("DeepSeek 回复的发言者或顺序与请求不符。");
                if (String.IsNullOrWhiteSpace(line.Text) || line.Text.Length > MaxLineCharacters || line.Text.IndexOf('<') >= 0 || line.Text.IndexOf('>') >= 0)
                    throw new InvalidDataException("DeepSeek 回复台词为空、过长或含富文本标记。");
                foreach (char c in line.Text)
                    if (Char.IsControl(c) && c != '\n' && c != '\r' && c != '\t') throw new InvalidDataException("DeepSeek 回复包含无效控制字符。");
            }
        }

        private static string StatusHint(int code)
        {
            if (code == 401 || code == 403) return "请检查 API 密钥和账户权限。";
            if (code == 402) return "请检查账户余额。";
            if (code == 429) return "请求频率受限，请稍后再试。";
            if (code >= 500) return "服务暂不可用，请稍后再试。";
            return "请检查接口地址、模型和请求配置。";
        }
    }
}
