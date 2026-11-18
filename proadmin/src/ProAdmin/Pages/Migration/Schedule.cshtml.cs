using ProAdmin.Migration;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Migration;

public class ScheduleModel : AppPageModel
{
    private readonly MigrationClient _client;

    public ScheduleModel(MigrationClient client) => _client = client;

    [BindProperty(SupportsGet = true)]
    public Guid? Id { get; set; }

    [BindProperty]
    public RequestForm Form { get; set; } = new();

    [BindProperty]
    public ScheduleForm Schedule { get; set; } = new();

    public List<MigrationEnvDto> Envs { get; private set; } = new();
    public string? Error { get; private set; }
    public bool IsNew => Id is null;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        ViewData["Section"] = "migration";
        try
        {
            Envs = (await _client.GetStatusAsync(ct)).Environments;
            if (Id is { } id)
            {
                var s = await _client.GetScheduleAsync(id, ct);
                if (s is null)
                    return NotFound();
                Form = RequestForm.From(s.Request);
                Schedule = ScheduleForm.From(s);
            }
        }
        catch (MigrationServiceException ex)
        {
            TempData["FlashError"] = ex.Message;
            return RedirectToPage("/Migration/Index");
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        ViewData["Section"] = "migration";
        try
        {
            ScheduleDto dto;
            if (Id is { } id)
                dto = await _client.GetScheduleAsync(id, ct) ?? new ScheduleDto { Id = id };
            else
                dto = new ScheduleDto { Id = Guid.NewGuid() };
            Schedule.Apply(dto);
            dto.Request = Form.ToDto();

            // Расписание проверяется так же, как ручной запуск: с ошибками проверки его не сохранить.
            var plan = await _client.PlanAsync(dto.Request, ct);
            var blockers = plan.Checks.Where(c => c.Level == "Blocker").Select(c => c.Text).ToList();
            if (blockers.Count > 0)
            {
                Error = "Перенос с такими параметрами сейчас невозможен: " + string.Join(" ", blockers);
                Envs = (await _client.GetStatusAsync(ct)).Environments;
                return Page();
            }

            var saved = await _client.SaveScheduleAsync(dto, CurrentSession.PilotLogin, ct);
            TempData["Flash"] = $"Расписание «{saved.Name}» сохранено: {saved.Description}, следующий запуск {saved.NextRunAt?.ToLocalTime():dd.MM HH:mm}.";
            return RedirectToPage("/Migration/Index");
        }
        catch (MigrationServiceException ex)
        {
            Error = ex.Message;
            try { Envs = (await _client.GetStatusAsync(ct)).Environments; } catch (MigrationServiceException) { }
            return Page();
        }
    }
}

public sealed class ScheduleForm
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Kind { get; set; } = "Weekly";
    public int IntervalValue { get; set; } = 24;
    public string IntervalUnit { get; set; } = "hours";
    public string TimeOfDay { get; set; } = "03:00";
    public List<int> Days { get; set; } = new();

    public static ScheduleForm From(ScheduleDto s)
    {
        var (value, unit) = s.IntervalMinutes % 1440 == 0 ? (s.IntervalMinutes / 1440, "days")
            : s.IntervalMinutes % 60 == 0 ? (s.IntervalMinutes / 60, "hours")
            : (s.IntervalMinutes, "minutes");
        return new ScheduleForm
        {
            Name = s.Name,
            Enabled = s.Enabled,
            Kind = s.Kind,
            IntervalValue = value,
            IntervalUnit = unit,
            TimeOfDay = s.TimeOfDay,
            Days = s.Days.ToList(),
        };
    }

    public void Apply(ScheduleDto s)
    {
        s.Name = Name.Trim();
        s.Enabled = Enabled;
        s.Kind = Kind == "Interval" ? "Interval" : "Weekly";
        s.IntervalMinutes = Math.Max(1, IntervalValue) * (IntervalUnit switch { "days" => 1440, "hours" => 60, _ => 1 });
        s.TimeOfDay = TimeOfDay;
        s.Days = Days.Distinct().OrderBy(d => d).ToList();
    }
}
