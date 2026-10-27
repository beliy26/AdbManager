using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AdbManager.Services;

/// <summary>Инструмент MCP: имя, описание и JSON Schema аргументов — для агента, поэтому по-английски.</summary>
public sealed record McpTool(string Name, string Title, string Description, JsonObject InputSchema, bool ReadOnly, bool Destructive,
    Func<JsonElement, Task<McpToolResult>> Handler);

/// <summary>Результат вызова инструмента: блоки content (текст или картинка) и признак ошибки.</summary>
public sealed record McpToolResult(JsonArray Content, bool IsError = false)
{
    public static McpToolResult Text(string text, bool isError = false) =>
        new([new JsonObject { ["type"] = "text", ["text"] = text }], isError);

    public static McpToolResult Image(byte[] data, string mimeType) =>
        new([new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(data), ["mimeType"] = mimeType }]);
}

/// <summary>Ошибка, понятная агенту: возвращается как результат с isError, без стека.</summary>
public sealed class McpToolException(string message) : Exception(message);

/// <summary>
/// MCP-сервер (Model Context Protocol) для агентов: транспорт Streamable HTTP на localhost,
/// без сессий и без потока событий от сервера — на каждый POST с JSON-RPC отвечаем JSON.
/// Работает, пока открыто окно программы; инструменты задаёт окно.
/// </summary>
public sealed class McpServer(int port, IReadOnlyList<McpTool> tools, string instructions) : IDisposable
{
    public const string EndpointPath = "/mcp";

    /// <summary>JSON без лишнего экранирования (кавычки, кириллица) — короче для агента.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private const string LatestProtocol = "2025-06-18";
    private static readonly string[] SupportedProtocols = ["2025-11-25", LatestProtocol, "2025-03-26", "2024-11-05"];

    private HttpListener? _listener;

    public int Port { get; } = port;
    public string Url => $"http://localhost:{Port}{EndpointPath}";
    public bool IsRunning => _listener?.IsListening == true;

    public event Action<string>? Log;

    /// <summary>
    /// Префикс «localhost» http.sys разрешает без прав администратора и сам отклоняет запросы с другим
    /// Host — это защищает от DNS rebinding. Порт занят — исключение HttpListenerException.
    /// </summary>
    public void Start()
    {
        if (IsRunning) return;
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{Port}/");
        listener.Start();
        _listener = listener;
        _ = Task.Run(() => AcceptLoopAsync(listener));
    }

    public void Stop()
    {
        var listener = _listener;
        _listener = null;
        try { listener?.Close(); }
        catch (ObjectDisposedException) { /* уже закрыт */ }
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (!listener.IsListening)
            {
                return; // сервер остановлен
            }
            catch (HttpListenerException)
            {
                continue;
            }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            if (request.Url?.AbsolutePath.TrimEnd('/') != EndpointPath)
            {
                response.StatusCode = 404;
                return;
            }
            // Запросы со страниц в браузере (кроме локальных) не принимаем — требование спецификации MCP.
            if (request.Headers["Origin"] is { Length: > 0 } origin && !IsLocalOrigin(origin))
            {
                response.StatusCode = 403;
                return;
            }
            if (request.HttpMethod != "POST")
            {
                // GET — поток уведомлений от сервера, DELETE — завершение сессии: не поддерживаем.
                response.StatusCode = 405;
                response.AddHeader("Allow", "POST");
                return;
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync();

            JsonNode? reply;
            try
            {
                var message = JsonNode.Parse(body);
                reply = message is JsonArray batch
                    ? new JsonArray((await Task.WhenAll(batch.Select(HandleMessageAsync))).Where(r => r is not null).ToArray())
                    : await HandleMessageAsync(message);
                if (reply is JsonArray { Count: 0 }) reply = null;
            }
            catch (JsonException)
            {
                reply = Error(null, -32700, "Parse error");
            }

            if (reply is null)
            {
                response.StatusCode = 202; // уведомления и ответы клиента
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString(JsonOptions));
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            Log?.Invoke("! MCP: " + ex.Message);
            try { response.StatusCode = 500; }
            catch (InvalidOperationException) { /* заголовки уже отправлены */ }
        }
        finally
        {
            try { response.Close(); }
            catch (Exception) { /* клиент отключился */ }
        }
    }

    private static bool IsLocalOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host == "localhost");

    /// <summary>Одно сообщение JSON-RPC. Для уведомлений (без id) ответа нет — null.</summary>
    private async Task<JsonNode?> HandleMessageAsync(JsonNode? message)
    {
        if (message is not JsonObject obj) return Error(null, -32600, "Invalid Request");
        var id = obj["id"]?.DeepClone();
        if (obj["method"]?.GetValue<string>() is not { } method) return null; // ответ клиента на наш запрос
        if (id is null) return null; // notifications/initialized и прочие уведомления

        var @params = obj["params"] as JsonObject;
        switch (method)
        {
            case "initialize":
                var requested = @params?["protocolVersion"]?.GetValue<string>();
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = requested is not null && SupportedProtocols.Contains(requested) ? requested : LatestProtocol,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "adb-manager",
                        ["title"] = "ADB Manager",
                        ["version"] = UpdateChecker.CurrentVersion.ToString(),
                    },
                    ["instructions"] = instructions,
                });

            case "ping":
                return Result(id, new JsonObject());

            case "tools/list":
                return Result(id, new JsonObject
                {
                    ["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t.Name,
                        ["title"] = t.Title,
                        ["description"] = t.Description,
                        ["inputSchema"] = t.InputSchema.DeepClone(),
                        ["annotations"] = new JsonObject
                        {
                            ["title"] = t.Title,
                            ["readOnlyHint"] = t.ReadOnly,
                            ["destructiveHint"] = t.Destructive,
                            ["openWorldHint"] = false,
                        },
                    }).ToArray()),
                });

            case "tools/call":
                var name = @params?["name"]?.GetValue<string>();
                if (tools.FirstOrDefault(t => t.Name == name) is not { } tool)
                    return Error(id, -32602, $"Unknown tool: {name}");

                var args = JsonSerializer.SerializeToElement(@params?["arguments"] ?? new JsonObject());
                McpToolResult result;
                try
                {
                    result = await tool.Handler(args);
                }
                catch (McpToolException ex)
                {
                    result = McpToolResult.Text(ex.Message, isError: true);
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"! MCP {name}: {ex.Message}");
                    result = McpToolResult.Text("Error: " + ex.Message, isError: true);
                }
                return Result(id, new JsonObject { ["content"] = result.Content, ["isError"] = result.IsError });

            default:
                return Error(id, -32601, $"Method not found: {method}");
        }
    }

    private static JsonObject Result(JsonNode id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
