using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Cloudshot
{
    /// <summary>
    /// 翻译服务：可切换服务商（有道 / MyMemory），凭据存在设置里。
    /// </summary>
    internal static class Translator
    {
        public sealed class Result
        {
            public bool Ok;
            public string Text = string.Empty;
            public string Provider = string.Empty;
            public string Error = string.Empty;
            public int ElapsedMs;
        }

        public static Result Translate(string text, bool fromChinese)
        {
            var result = new Result();
            if (string.IsNullOrWhiteSpace(text))
            {
                result.Error = "没有可翻译的文字";
                return result;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (Settings.TranslateProvider == "mymemory")
                {
                    result.Text = TranslateMyMemory(text, fromChinese, out result.Error);
                    result.Provider = "MyMemory";
                }
                else if (!string.IsNullOrEmpty(Settings.YoudaoAppKey) && !string.IsNullOrEmpty(Settings.YoudaoSecret))
                {
                    result.Text = TranslateYoudao(text, fromChinese, out result.Error);
                    result.Provider = "有道翻译";
                }
                else
                {
                    // 没配有道凭据时自动退回免 Key 的 MyMemory
                    result.Text = TranslateMyMemory(text, fromChinese, out result.Error);
                    result.Provider = "MyMemory（未配置有道凭据）";
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.GetType().Name + ": " + ex.Message;
            }
            sw.Stop();
            result.ElapsedMs = (int)sw.ElapsedMilliseconds;
            result.Ok = !string.IsNullOrEmpty(result.Text) && string.IsNullOrEmpty(result.Error);
            return result;
        }

        #region 有道

        private const string YoudaoEndpoint = "https://openapi.youdao.com/api";

        private sealed class YoudaoPayload
        {
            public string errorCode;
            public string[] translation;
        }

        private sealed class MyMemoryPayload
        {
            public MyMemoryData responseData;
        }

        private sealed class MyMemoryData
        {
            public string translatedText;
        }

        private static string TranslateYoudao(string text, bool fromChinese, out string error)
        {
            error = null;
            string from = fromChinese ? "zh-CHS" : "en";
            string to = fromChinese ? "en" : "zh-CHS";

            string salt = Guid.NewGuid().ToString();
            string curtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            string sign = Sha256Hex(Settings.YoudaoAppKey + TruncateForSign(text) + salt + curtime + Settings.YoudaoSecret);

            var query = new StringBuilder();
            query.Append("q=").Append(Uri.EscapeDataString(text));
            query.Append("&from=").Append(from);
            query.Append("&to=").Append(to);
            query.Append("&appKey=").Append(Uri.EscapeDataString(Settings.YoudaoAppKey));
            query.Append("&salt=").Append(salt);
            query.Append("&sign=").Append(sign);
            query.Append("&signType=v3");
            query.Append("&curtime=").Append(curtime);

            string body = HttpGet(YoudaoEndpoint + "?" + query);
            var payload = new JavaScriptSerializer().Deserialize<YoudaoPayload>(body);
            if (payload == null)
            {
                error = "翻译服务返回了无法解析的内容";
                return null;
            }

            if (!string.IsNullOrEmpty(payload.errorCode) && payload.errorCode != "0")
            {
                error = DescribeYoudaoError(payload.errorCode);
                return null;
            }

            if (payload.translation != null && payload.translation.Length > 0)
            {
                var sb = new StringBuilder();
                foreach (var line in payload.translation)
                {
                    if (sb.Length > 0) sb.Append("\n");
                    sb.Append(line);
                }
                return sb.ToString();
            }

            error = "翻译结果为空（原始返回：" +
                    (body.Length > 200 ? body.Substring(0, 200) : body) + "）";
            return null;
        }

        /// <summary>
        /// 有道 v3 签名规则：长度 &gt; 20 时取「前 10 + 长度 + 后 10」。
        /// </summary>
        private static string TruncateForSign(string q)
        {
            if (q.Length <= 20) return q;
            return q.Substring(0, 10) + q.Length.ToString(CultureInfo.InvariantCulture) + q.Substring(q.Length - 10);
        }

        private static string DescribeYoudaoError(string code)
        {
            switch (code)
            {
                case "101": return "缺少必填参数（检查 App ID / 密钥是否填全）";
                case "102": return "不支持的语言类型";
                case "103": return "翻译文本过长";
                case "108": return "应用 ID 无效，或该应用未开通「文本翻译」服务";
                case "110": return "无相关服务的有效实例，请检查控制台是否已开通服务";
                case "111": return "开发者账号无效";
                case "112": return "请求的服务无效";
                case "113": return "待翻译内容为空";
                case "202": return "签名校验失败（检查应用密钥是否与该应用 ID 匹配）";
                case "206": return "时间戳无效（本机时间不准，请校准系统时间）";
                case "401": return "账户已欠费";
                case "411": return "访问频率受限";
                default: return "翻译失败，有道返回错误码 " + code;
            }
        }

        #endregion

        #region MyMemory（免 Key 备用）

        private static string TranslateMyMemory(string text, bool fromChinese, out string error)
        {
            error = null;
            string pair = fromChinese ? "zh-CN|en" : "en|zh-CN";
            string url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text) + "&langpair=" + Uri.EscapeDataString(pair);
            string body = HttpGet(url);
            var payload = new JavaScriptSerializer().Deserialize<MyMemoryPayload>(body);
            if (payload == null || payload.responseData == null)
            {
                error = "MyMemory 返回了无法解析的内容";
                return null;
            }

            string translated = payload.responseData.translatedText;
            if (string.IsNullOrWhiteSpace(translated) || translated.IndexOf("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                error = "MyMemory 免费额度已用完，请配置有道翻译 Key";
                return null;
            }
            return translated;
        }

        #endregion

        private static string HttpGet(string url)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = 12000;
            request.UserAgent = "Cloudshot/1.0";
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new System.IO.StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static string Sha256Hex(string text)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }
}
