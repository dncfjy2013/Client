using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Client.Core.HttpClientClass
{
    public class HttpClientInstance : IDisposable
    {
        private readonly string _serverUrl;
        private readonly HttpClient _httpClient;
        private bool _disposed;

        public HttpClientInstance(string serverUrl)
        {
            if (string.IsNullOrEmpty(serverUrl))
            {
                throw new ArgumentNullException(nameof(serverUrl), "服务器 URL 不能为空。");
            }
            _serverUrl = serverUrl;
            _httpClient = new HttpClient();
        }

        public async Task SendGetRequest(Dictionary<string, string> parameters = null)
        {
            var url = BuildUrlWithParameters(parameters);
            await SendRequestAndHandleResponse(HttpMethod.Get, null, url);
        }

        public async Task SendPostRequest(string postData, Dictionary<string, string> parameters = null)
        {
            var content = CreateStringContent(postData);
            var url = BuildUrlWithParameters(parameters);
            await SendRequestAndHandleResponse(HttpMethod.Post, content, url);
        }

        public async Task SendPutRequest(string putData, Dictionary<string, string> parameters = null)
        {
            var content = CreateStringContent(putData);
            var url = BuildUrlWithParameters(parameters);
            await SendRequestAndHandleResponse(HttpMethod.Put, content, url);
        }

        public async Task SendDeleteRequest(Dictionary<string, string> parameters = null)
        {
            var url = BuildUrlWithParameters(parameters);
            await SendRequestAndHandleResponse(HttpMethod.Delete, null, url);
        }

        private string BuildUrlWithParameters(Dictionary<string, string> parameters)
        {
            if (parameters == null || parameters.Count == 0)
            {
                return _serverUrl;
            }

            var queryString = string.Join("&", parameters.Select(p => $"{p.Key}={p.Value}"));
            return $"{_serverUrl}?{queryString}";
        }

        private async Task SendRequestAndHandleResponse(HttpMethod method, HttpContent content, string requestUrl)
        {
            try
            {
                var response = await SendRequest(method, content, requestUrl);
                await HandleResponse(response, method.Method);
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"发送 {method.Method} 请求时发生网络错误: {ex.Message}");
            }
            catch (TaskCanceledException ex)
            {
                Console.WriteLine($"发送 {method.Method} 请求时任务被取消: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"发送 {method.Method} 请求时发生未知错误: {ex.Message}");
            }
        }

        private async Task<HttpResponseMessage> SendRequest(HttpMethod method, HttpContent content = null, string requestUrl = null)
        {
            if (string.IsNullOrEmpty(requestUrl))
            {
                requestUrl = _serverUrl;
            }

            var request = new HttpRequestMessage(method, requestUrl);
            if (content != null)
            {
                request.Content = content;
            }
            return await _httpClient.SendAsync(request);
        }

        private async Task HandleResponse(HttpResponseMessage response, string method)
        {
            try
            {
                if (response.IsSuccessStatusCode)
                {
                    string result = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"{method} 请求响应: {result}");
                }
                else
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"{method} 请求失败，状态码: {response.StatusCode}，错误信息: {errorContent}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"处理 {method} 请求响应时发生错误: {ex.Message}");
            }
        }

        private StringContent CreateStringContent(string data)
        {
            if (string.IsNullOrEmpty(data))
            {
                data = string.Empty;
            }
            return new StringContent(data, Encoding.UTF8, "text/plain");
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _httpClient.Dispose();
                }
                _disposed = true;
            }
        }
    }
}