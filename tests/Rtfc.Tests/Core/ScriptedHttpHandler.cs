using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Rtfc.Tests.Core;

/// <summary>Answers scripted JSON per URL fragment, records the requests, and can play a broken service. For adapter tests without a network.</summary>
public sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly List<(string Fragment, Func<string, bool>? When, JsonObject Body)> _replies = [];

    public List<string> Requests { get; } = [];
    public string? Authorization { get; private set; }
    public HttpStatusCode? Status { get; init; }
    public int? RetryAfterSeconds { get; init; }

    public void Reply(string fragment, JsonObject body, Func<string, bool>? when = null) => _replies.Add((fragment, when, body));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.OriginalString;
        Requests.Add(url);
        Authorization = request.Headers.Authorization?.ToString();
        if (Status is { } status)
        {
            var failure = new HttpResponseMessage(status) { Content = new StringContent("""{"message":"nope","errorMessages":["nope"]}""", Encoding.UTF8, "application/json") };
            if (RetryAfterSeconds is { } seconds)
            {
                failure.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            }

            return Task.FromResult(failure);
        }

        var reply = _replies.FirstOrDefault(r => url.Contains(r.Fragment, StringComparison.Ordinal) && (r.When?.Invoke(url) ?? true));
        if (reply.Body is null)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply.Body.ToJsonString(), Encoding.UTF8, "application/json") });
    }
}
