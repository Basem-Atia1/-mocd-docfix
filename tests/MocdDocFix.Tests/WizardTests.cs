using MocdDocFix.Cli;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class WizardTests
{
    private readonly List<string> _ran = new();
    private IReadOnlyList<string>? _targeted;
    private ScanOutcome _scan = Scan(
        File(3, "a.pdf", "Employee Appointment Request", "Medical Certificate"),
        File(3, "b.pdf", "Employee Appointment Request", "Medical Certificate"),
        File(5, "c.pdf", "GAM - Attendance", "Approved Attendance List"));

    private static PickableFile File(int group, string name, string service, string docType) =>
        new(group, Guid.NewGuid().ToString(), name, service, docType);

    private static ScanOutcome Scan(params PickableFile[] files) =>
        new("376 broken, 88 nothing to do", new[] { "Employee Appointment 188", "By-Laws 15" }, files);

    private Wizard Build(ScriptedPrompts prompts) =>
        new(prompts, "dev", isProduction: false, "https://crm/MoCD", "http://files:83",
            new WizardActions(
                ScanAsync: () => { _ran.Add("scan"); return Task.FromResult(_scan); },
                ClassifyAsync: ids =>
                {
                    _ran.Add("check");
                    _targeted = ids;
                    return Task.FromResult(_scan with { Fixable = _scan.Fixable.Take(ids.Count).ToList() });
                },
                BackupAsync: () => { _ran.Add("backup"); return Task.FromResult(StepOutcome.Of("23 saved", "0 quarantined")); },
                MigrateAsync: () => { _ran.Add("migrate"); return Task.FromResult(StepOutcome.Of("23 migrated")); },
                DeleteAsync: () => { _ran.Add("delete"); return Task.FromResult(StepOutcome.Of("23 deleted")); }));

    private async Task<ScriptedPrompts> Run(params string[] input)
    {
        var prompts = new ScriptedPrompts(input);
        await Build(prompts).RunAsync(CancellationToken.None);
        return prompts;
    }

    // ---- the opening question ----

    [Fact]
    public async Task The_banner_names_the_environment_and_both_urls()
    {
        var prompts = await Run("5");

        Assert.True(prompts.Said("dev"));
        Assert.True(prompts.Said("https://crm/MoCD"));
        Assert.True(prompts.Said("http://files:83"));
    }

    [Fact]
    public async Task Production_is_marked_in_the_banner()
    {
        var prompts = new ScriptedPrompts("5");
        await new Wizard(prompts, "prod", true, "https://crm", "http://files",
            new WizardActions(
                () => Task.FromResult(_scan),
                _ => Task.FromResult(_scan),
                () => Task.FromResult(StepOutcome.Of("")),
                () => Task.FromResult(StepOutcome.Of("")),
                () => Task.FromResult(StepOutcome.Of(""))))
            .RunAsync(CancellationToken.None);

        Assert.True(prompts.Said("*** PRODUCTION ***"));
    }

    [Fact]
    public async Task Quitting_runs_nothing()
    {
        await Run("5");

        Assert.Empty(_ran);
    }

    [Fact]
    public async Task Changing_environment_reports_that_and_stops_the_wizard()
    {
        var prompts = new ScriptedPrompts("4");

        var exit = await Build(prompts).RunAsync(CancellationToken.None);

        Assert.Equal(WizardExit.ChangeEnvironment, exit);
        Assert.Empty(_ran);
    }

    [Fact]
    public async Task Report_only_scans_and_changes_nothing()
    {
        await Run("3", "5");

        Assert.Equal(new[] { "scan" }, _ran);
    }

    [Fact]
    public async Task Report_only_says_plainly_that_nothing_changed()
    {
        var prompts = await Run("3", "5");

        Assert.True(prompts.Said("Nothing was changed"));
    }

    // ---- full mode, and the gate between every step ----

    [Fact]
    public async Task Full_mode_runs_all_five_steps_when_every_gate_is_passed()
    {
        await Run("2", "1", "y", "1", "y", "1", "y", "5");

        Assert.Equal(new[] { "scan", "backup", "migrate", "delete" }, _ran);
    }

    [Fact]
    public async Task Stopping_at_the_first_gate_runs_nothing_further()
    {
        await Run("2", "3", "5");

        Assert.Equal(new[] { "scan" }, _ran);
    }

    [Fact]
    public async Task Stopping_at_the_backup_gate_leaves_crm_untouched()
    {
        await Run("2", "1", "y", "3", "5");

        Assert.Equal(new[] { "scan", "backup" }, _ran);
    }

    [Fact]
    public async Task Refusing_to_contact_the_file_server_stops_before_backup()
    {
        await Run("2", "1", "n", "5");

        Assert.Equal(new[] { "scan" }, _ran);
    }

    [Fact]
    public async Task Refusing_the_upload_warning_stops_before_migrate()
    {
        await Run("2", "1", "y", "1", "n", "5");

        Assert.Equal(new[] { "scan", "backup" }, _ran);
    }

    [Fact]
    public async Task Refusing_the_delete_warning_stops_before_delete()
    {
        await Run("2", "1", "y", "1", "y", "1", "n", "5");

        Assert.Equal(new[] { "scan", "backup", "migrate" }, _ran);
    }

    [Fact]
    public async Task A_gate_says_which_step_it_is_and_what_comes_next()
    {
        var prompts = await Run("2", "3", "5");

        Assert.True(prompts.Said("Step 1 of 5"));
        Assert.True(prompts.Said("back up all 3 files"));
    }

    [Fact]
    public async Task Show_details_prints_them_again_and_re_asks_rather_than_moving_on()
    {
        var once = await Run("2", "3", "5");
        var twice = await Run("2", "2", "3", "5");

        int Count(ScriptedPrompts p) =>
            p.Messages.Count(m => m.Contains("Employee Appointment 188", StringComparison.Ordinal));

        // Asking for details prints them a second time, and the gate comes back.
        Assert.Equal(Count(once) + 1, Count(twice));
        Assert.Equal(2, twice.Messages.Count(m => m.Contains("Step 1 finished", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_step_numbers_never_appear_to_skip_one()
    {
        // Migrate does steps 3 and 4 in one pass, so the gate says so rather than jumping 2 -> 4.
        var prompts = await Run("2", "1", "y", "1", "y", "1", "y", "5");

        Assert.True(prompts.Said("Step 1 of 5"));
        Assert.True(prompts.Said("Step 2 of 5"));
        Assert.True(prompts.Said("Step 3 and 4 of 5"));
        Assert.True(prompts.Said("Step 5 of 5"));
        Assert.False(prompts.Said("Step 4 of 5"));
        Assert.False(prompts.Said("Step 3 of 5"));
    }

    [Fact]
    public async Task Stopping_explains_that_it_is_safe_to_stop()
    {
        var prompts = await Run("2", "3", "5");

        Assert.True(prompts.Said("old files are untouched"));
    }

    [Fact]
    public async Task Full_mode_stops_early_when_there_is_nothing_to_fix()
    {
        _scan = Scan();

        var prompts = await Run("2", "5");

        Assert.Equal(new[] { "scan" }, _ran);
        Assert.True(prompts.Said("Nothing here needs fixing"));
    }

    // ---- targeted mode: the same gates, not a bulk run ----
    //
    // Answering the file-server question used to set off backup, upload and delete in one go.
    // Every one of these scripts stops somewhere, and asserts what did NOT run.

    [Fact]
    public async Task Targeted_checks_before_it_touches_the_file_server()
    {
        // targeted -> type -> check -> STOP at the first gate
        await Run("1", "2", "a.jpg", "3", "5");

        Assert.Equal(new[] { "check" }, _ran);
    }

    [Fact]
    public async Task Targeted_stops_after_backup_and_asks_before_uploading()
    {
        // … -> gate 1 continue -> contact file server -> backup -> STOP before upload
        await Run("1", "2", "a.jpg", "1", "y", "3", "5");

        Assert.Equal(new[] { "check", "backup" }, _ran);
    }

    [Fact]
    public async Task Targeted_asks_again_before_uploading_even_after_the_gate()
    {
        // gate 1 -> file server y -> backup -> gate 2 continue -> refuse the upload warning
        await Run("1", "2", "a.jpg", "1", "y", "1", "n", "5");

        Assert.Equal(new[] { "check", "backup" }, _ran);
    }

    [Fact]
    public async Task Targeted_stops_after_uploading_and_asks_before_deleting()
    {
        await Run("1", "2", "a.jpg", "1", "y", "1", "y", "3", "5");

        Assert.Equal(new[] { "check", "backup", "migrate" }, _ran);
    }

    [Fact]
    public async Task Targeted_runs_all_five_steps_only_when_every_gate_is_passed()
    {
        await Run("1", "2", "a.jpg", "1", "y", "1", "y", "1", "y", "5");

        Assert.Equal(new[] { "check", "backup", "migrate", "delete" }, _ran);
    }

    [Fact]
    public async Task Targeted_and_full_stop_at_exactly_the_same_places()
    {
        var targeted = await Run("1", "2", "a.jpg", "1", "y", "1", "y", "1", "y", "5");
        var targetedSteps = targeted.Messages.Where(m => m.Contains("Step ", StringComparison.Ordinal)).ToList();

        _ran.Clear();
        var full = await Run("2", "1", "y", "1", "y", "1", "y", "5");
        var fullSteps = full.Messages.Where(m => m.Contains("Step ", StringComparison.Ordinal)).ToList();

        // Same number of gates, same numbering — only the name of step 1 differs.
        Assert.Equal(fullSteps.Count, targetedSteps.Count);
        Assert.True(targeted.Said("Step 1 of 5 — Check"));
        Assert.True(full.Said("Step 1 of 5 — Scan"));
    }

    [Fact]
    public async Task Typed_identifiers_are_split_on_commas_and_trimmed()
    {
        await Run("1", "2", " a.jpg , b.jpg ", "3", "5");

        Assert.Equal(new[] { "a.jpg", "b.jpg" }, _targeted);
    }

    [Fact]
    public async Task Picking_from_the_last_scan_needs_no_guids_typed()
    {
        // targeted -> from the scan -> group 3 -> rows 1 and 2 -> stop at the first gate
        await Run("1", "1", "1", "1,2", "3", "5");

        Assert.Equal(new[] { "scan", "check" }, _ran);
        Assert.Equal(2, _targeted!.Count);
        Assert.Equal(_scan.Fixable.Take(2).Select(f => f.Identifier), _targeted);
    }

    [Fact]
    public async Task The_group_list_shows_each_group_with_its_count_and_its_label()
    {
        var prompts = await Run("1", "1", "1", "1", "3", "5");

        Assert.True(prompts.Said("Group 3"));
        Assert.True(prompts.Said("Group 5"));
        Assert.True(prompts.Said("[   2]"));   // two files in group 3
        Assert.True(prompts.Said("[   1]"));   // one in group 5
        Assert.True(prompts.Said("path holds a name instead of an id"));
    }

    [Fact]
    public async Task All_selects_every_file_in_the_group()
    {
        await Run("1", "1", "1", "all", "3", "5");

        Assert.Equal(2, _targeted!.Count);
    }

    [Fact]
    public async Task A_range_works_too()
    {
        await Run("1", "1", "1", "1-2", "3", "5");

        Assert.Equal(2, _targeted!.Count);
    }

    [Fact]
    public async Task A_bad_selection_is_explained_and_asked_again()
    {
        var prompts = await Run("1", "1", "1", "9", "1", "3", "5");

        Assert.True(prompts.Said("1 to 2"));
        Assert.Single(_targeted!);
    }

    [Fact]
    public async Task Backing_out_of_the_file_list_returns_to_the_group_question()
    {
        // group 3 -> b -> group 5 -> row 1 -> stop
        await Run("1", "1", "1", "b", "2", "1", "3", "5");

        Assert.Single(_targeted!);
        Assert.Equal(_scan.Fixable[2].Identifier, _targeted![0]);
    }

    [Fact]
    public async Task Refusing_the_file_server_downloads_nothing()
    {
        await Run("1", "2", "a.jpg", "1", "n", "5");

        Assert.Equal(new[] { "check" }, _ran);
    }

    [Fact]
    public async Task Typing_nothing_returns_to_the_menu_without_running()
    {
        await Run("1", "2", "", "5");

        Assert.Empty(_ran);
    }

    [Fact]
    public async Task Targeted_reuses_the_scan_it_already_ran_rather_than_scanning_twice()
    {
        await Run("3", "1", "1", "1", "1", "3", "5");

        Assert.Equal(new[] { "scan", "check" }, _ran);
    }

    [Fact]
    public async Task The_chosen_files_are_listed_back_before_anything_is_contacted()
    {
        var prompts = await Run("1", "2", "cert.jpg", "3", "5");

        Assert.True(prompts.Said("1 file(s) chosen"));
        Assert.True(prompts.Said("cert.jpg"));
    }

    // ---- nothing can end the run by accident ----

    [Fact]
    public async Task An_unusable_answer_at_the_opening_question_re_asks()
    {
        var prompts = await Run("banana", "5");

        Assert.True(prompts.Said("Type the number"));
        Assert.Empty(_ran);
    }

    [Fact]
    public async Task A_question_mark_explains_the_modes()
    {
        var prompts = await Run("?", "5");

        Assert.True(prompts.Said("The safest way to start"));
    }

    [Fact]
    public async Task The_wizard_returns_to_its_question_after_each_mode_finishes()
    {
        await Run("3", "3", "5");

        Assert.Equal(new[] { "scan", "scan" }, _ran);
    }
}
