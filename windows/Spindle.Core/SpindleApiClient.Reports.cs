namespace Spindle.Core;

public sealed partial class SpindleApiClient
{
    public Task<SummaryReport> GetSummaryReportAsync(Guid workspaceId, SummaryReportRequest request, CancellationToken ct = default) => PostAsync<SummaryReport>($"{Workspace(workspaceId)}/reports/summary", request, ct);
    public Task<ExportFile> ExportSummaryReportAsync(Guid workspaceId, SummaryReportRequest request, string format, CancellationToken ct = default) => ExportAsync($"{Workspace(workspaceId)}/reports/summary/export?format={Uri.EscapeDataString(format)}", request, ct);
    public Task<DetailedReport> GetDetailedReportAsync(Guid workspaceId, DetailedReportRequest request, CancellationToken ct = default) => PostAsync<DetailedReport>($"{Workspace(workspaceId)}/reports/detailed", request, ct);
    public Task<ExportFile> ExportDetailedReportAsync(Guid workspaceId, DetailedReportRequest request, string format, CancellationToken ct = default) => ExportAsync($"{Workspace(workspaceId)}/reports/detailed/export?format={Uri.EscapeDataString(format)}", request, ct);
    public Task<WeeklyReport> GetWeeklyReportAsync(Guid workspaceId, WeeklyReportRequest request, CancellationToken ct = default) => PostAsync<WeeklyReport>($"{Workspace(workspaceId)}/reports/weekly", request, ct);
    public Task<ExportFile> ExportWeeklyReportAsync(Guid workspaceId, WeeklyReportRequest request, string format, CancellationToken ct = default) => ExportAsync($"{Workspace(workspaceId)}/reports/weekly/export?format={Uri.EscapeDataString(format)}", request, ct);
    private async Task<ExportFile> ExportAsync(string path, object body, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, ct);
        var disposition = response.Content.Headers.ContentDisposition;
        var fileName = Path.GetFileName((disposition?.FileNameStar ?? disposition?.FileName ?? "report").Trim('"'));
        return new(await response.Content.ReadAsByteArrayAsync(ct), fileName, response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }
}
