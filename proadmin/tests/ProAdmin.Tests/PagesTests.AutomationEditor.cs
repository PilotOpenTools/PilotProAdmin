using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProAdmin.Infrastructure;
using ProAdmin.Settings;

namespace ProAdmin.Tests;

public sealed partial class PagesTests
{
    [Theory]
    [InlineData("test", "dev", "feature/KTN-42-notify", false)]
    [InlineData("prod", "main", "hotfix/KTN-42-notify", true)]
    [InlineData("prod", "main", "hotfix/KTN-42-notify", false)]
    public async Task EditorChecksAndSavesAgainstTheSelectedBranch(string env, string baseBranch, string branch, bool taskRequired)
    {
        var editor = $"/Automations/{env}/Edit?name=Notify";
        var directLink = editor + "&branch=" + Uri.EscapeDataString(branch);
        const string head = "abc0123";
        const string fileCommit = "abc0456";
        const string prefix = "[{\"when\":\"create\",\"_comment_\":\"Version: ";
        const string sameVersion = prefix + "1.0.2, updated\"}]";
        JsonElement? commit = null;

        var dataSubdirectory = $"automation-editor-{Guid.NewGuid():N}";
        using var app = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<AppOptions>(options =>
                options.DataDirectory = Path.Combine(options.DataDirectory, dataSubdirectory));
            services.AddHttpClient("gitlab").ConfigurePrimaryHttpMessageHandler(() => new EditorGitLabHandler(async request =>
            {
                var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
                if (path.EndsWith("/repository/commits"))
                {
                    commit = await request.Content!.ReadFromJsonAsync<JsonElement>();
                    return GitLabAnswer("{\"short_id\":\"987abc\"}");
                }
                var branchEntry = new { name = branch, commit = new { id = head } };
                if (path.EndsWith("/repository/branches/" + branch))
                    return GitLabAnswer(JsonSerializer.Serialize(branchEntry));
                if (path.EndsWith("/repository/branches"))
                    return GitLabAnswer(JsonSerializer.Serialize(new[] { branchEntry }));
                if (path.EndsWith("/repository/files/Notify.json"))
                {
                    var version = request.RequestUri.Query == "?ref=" + baseBranch ? "1.0.1" : "1.0.2";
                    return GitLabAnswer(JsonSerializer.Serialize(new { content = prefix + version + "\"}]", last_commit_id = fileCommit }));
                }
                if (path.EndsWith("/groups/automation/projects"))
                    return GitLabAnswer("[{\"name\":\"Notify\",\"path\":\"notify\",\"path_with_namespace\":\"automation/notify\"}]");
                if (path.Contains("/members/all/"))
                    return GitLabAnswer("{\"access_level\":30}");
                if (path.EndsWith("/groups/automation"))
                    return GitLabAnswer("{\"id\":1}");
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
        }));
        var hotfix = env == AdminSettings.Prod;
        using var client = ClientFor(app, _factory.NewSession(withGitLab: true, prodAdmin: hotfix, services: app.Services));
        if (hotfix)
        {
            var store = app.Services.GetRequiredService<SettingsStore>();
            var settings = store.Current;
            var prod = settings.Find(env)!;
            prod.PilotUrl = Unreachable + "/pilot-ice_ru";
            prod.RequireTaskReference = taskRequired;
            store.Save(settings);

            var html = WebUtility.HtmlDecode(await client.GetStringAsync(directLink));
            var taskLabel = taskRequired ? "обязательно" : "если есть";
            Assert.Matches($$"""<label class="field">\s*<span>Задача \({{taskLabel}}\)</span>\s*<input[^>]+id="task"[^>]*>\s*</label>\s*<div id="new-branch-fields"\s""", html);
        }

        var token = await AntiforgeryToken(client, directLink);
        Task<HttpResponseMessage> Submit(string handler, string selected, string json) => client.PostAsync(editor + "&handler=" + handler,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Form.WorkBranch"] = selected,
                ["Form.BranchSlug"] = "new-branch",
                ["Form.Triggers"] = json,
                ["Form.Description"] = "Изменение",
                ["Form.TaskReference"] = taskRequired ? "KTN-42" : "",
            }));

        using var validate = await Submit("Validate", branch, sameVersion);
        var messages = (await validate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");
        Assert.Contains(messages.EnumerateArray(), item => item.GetProperty("level").GetString() == "error" &&
            item.GetProperty("text").GetString()!.Contains($"в ветке {branch} - 1.0.2"));

        using var blocked = await Submit("SaveBranch", branch, sameVersion);
        Assert.Equal(HttpStatusCode.OK, blocked.StatusCode);
        Assert.Contains($"в ветке {branch} - 1.0.2", await Text(blocked));
        Assert.Null(commit);

        using var validateNew = await Submit("Validate", "+new", sameVersion);
        var newMessages = (await validateNew.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");
        Assert.Contains(newMessages.EnumerateArray(), item => item.GetProperty("text").GetString() == "Версия повышена: 1.0.1 / 1.0.2.");

        using var saved = await Submit("SaveBranch", branch, prefix + "1.0.3, updated\"}]");
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.True(commit.HasValue);
        Assert.Equal(branch, commit.Value.GetProperty("branch").GetString());
        Assert.Equal(taskRequired ? "Notify: KTN-42, Изменение" : "Notify: Изменение", commit.Value.GetProperty("commit_message").GetString());
        var action = Assert.Single(commit.Value.GetProperty("actions").EnumerateArray());
        Assert.Equal(fileCommit, action.GetProperty("last_commit_id").GetString());
    }

    private sealed class EditorGitLabHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}
