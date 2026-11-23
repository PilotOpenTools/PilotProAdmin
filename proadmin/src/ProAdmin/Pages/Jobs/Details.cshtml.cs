using ProAdmin.Infrastructure;
using ProAdmin.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Jobs;

public class DetailsModel : AppPageModel
{
    private readonly JobStore _store;

    public DetailsModel(JobStore store) => _store = store;

    public PublishJob Job { get; private set; } = null!;

    public IActionResult OnGet(Guid id)
    {
        ViewData["Section"] = "jobs";
        var job = _store.Get(id)?.Snapshot();
        // 404, а не 403: чужая операция не должна подтверждать, что такой идентификатор существует.
        if (job is null || !JobStore.CanSee(job, CurrentSession, IsAdmin))
            return NotFound();

        Job = job;
        return Page();
    }

    /// <summary>Опрос состояния со страницы, пока операция не завершится.</summary>
    public IActionResult OnGetStatus(Guid id)
    {
        var job = _store.Get(id)?.Snapshot();
        if (job is null || !JobStore.CanSee(job, CurrentSession, IsAdmin))
            return NotFound();

        return new JsonResult(new
        {
            status = job.Status.ToString(),
            finished = job.IsFinished,
            error = job.Error,
            commitUrl = SafeUrl.Http(job.CommitUrl),
            mergeRequestUrl = SafeUrl.Http(job.MergeRequestUrl),
            pipelineUrl = SafeUrl.Http(job.PipelineUrl),
            log = job.Log.Select(e => new { time = e.Time.ToLocalTime().ToString("HH:mm:ss"), level = e.Level, text = e.Text, url = SafeUrl.Http(e.Url), details = e.Details }),
        });
    }
}
