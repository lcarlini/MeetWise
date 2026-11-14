using System.Net;
using System.Text;

namespace MeetWise.Tests;

internal sealed class StubHandler : HttpMessageHandler
{
    public readonly List<(HttpRequestMessage Request, string Body)> Calls = new();
    public Func<HttpRequestMessage, string, (HttpStatusCode, string)> Respond { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Calls.Add((request, body));
        var (status, json) = Respond(request, body);
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
