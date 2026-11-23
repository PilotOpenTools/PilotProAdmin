using ProAdmin.Jobs;

namespace ProAdmin.Pages.Jobs;

public class IndexModel : AppPageModel
{
    private readonly JobStore _store;

    public IndexModel(JobStore store) => _store = store;

    public IReadOnlyList<PublishJob> Jobs { get; private set; } = Array.Empty<PublishJob>();

    public bool ShowAll { get; private set; }

    public void OnGet()
    {
        ViewData["Section"] = "jobs";
        var session = CurrentSession;
        var all = IsAdmin;
        ShowAll = all;
        Jobs = _store.Recent(200).Where(j => JobStore.CanSee(j, session, all)).ToList();
    }
}
