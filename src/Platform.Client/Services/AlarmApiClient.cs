using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Platform.Client.Services;

public sealed record AlarmDto(
    long Id, string AlarmNo, long? DeviceId, int Level, string Code, string Message,
    DateTime RaisedAt, DateTime? ClearedAt, long? AckedBy, DateTime? AckedAt);

/// <summary>报警 API 客户端（C1 查询 + C3 确认；确认需要操作员 ID 头，与写指令同权口径）。</summary>
public sealed class AlarmApiClient(string baseAddress)
{
    private readonly HttpClient _httpClient = HttpClientHolder.Get(baseAddress, TimeSpan.FromSeconds(5));

    public async Task<IReadOnlyList<AlarmDto>> GetAlarmsAsync(bool active, CancellationToken cancellationToken = default)
    {
        var alarms = await _httpClient.GetFromJsonAsync<AlarmDto[]>($"/api/alarms?active={active}", cancellationToken);
        return alarms ?? [];
    }

    public async Task<bool> AckAsync(long alarmId, long operatorId, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/alarms/{alarmId}/ack");
        request.Headers.Add("X-Operator", operatorId.ToString());
        var response = await _httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
