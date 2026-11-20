using AiUsage.Data;
using AiUsage.Ui;
using Xunit;

namespace AiUsage.Linux.Tests;

public sealed class StatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static Status.Limit Limit(ProviderId id, string label, float left, double resetHours = 24, string slot = "", string? name = null) =>
        new(new ProviderUsage { Id = id, Slot = slot, AccountName = name },
            new UsageWindow(label, left, Now.AddHours(resetHours)));

    [Fact]
    public void ForBar_LeavesOutEmptyLimits()
    {
        var limits = new[]
        {
            Limit(ProviderId.Kimi, "Monthly", 0),
            Limit(ProviderId.Claude, "7d", 31),
            Limit(ProviderId.Mistral, "Vibe", 0.3f),
            Limit(ProviderId.ChatGpt, "7d", 68),
        };

        var shown = Status.ForBar(limits, 4, Now);

        Assert.Equal(["Claude", "ChatGpt"], shown.Select(l => l.Usage.Id.ToString()));
    }

    [Fact]
    public void ForBar_TakesTheClosestThenTheSoonerReset()
    {
        var limits = new[]
        {
            Limit(ProviderId.Claude, "5h", 40, resetHours: 3),
            Limit(ProviderId.ChatGpt, "7d", 40, resetHours: 1),
            Limit(ProviderId.Kimi, "7d", 11),
            Limit(ProviderId.Mistral, "Vibe", 90),
        };

        var shown = Status.ForBar(limits, 3, Now);

        Assert.Equal(["Kimi", "ChatGpt", "Claude"], shown.Select(l => l.Usage.Id.ToString()));
    }

    [Fact]
    public void BarLabels_AreShortAndStillTellLimitsApart()
    {
        var labels = Status.BarLabels(
        [
            Limit(ProviderId.Claude, "7d", 31),
            Limit(ProviderId.Claude, "5h", 38),
            Limit(ProviderId.Claude, "Fable 7d", 57),
            Limit(ProviderId.Antigravity, "Claude & GPT weekly limit", 64),
        ]);

        Assert.Equal(["Weekly", "5h", "Fable", "Claude & GP…"], labels);
    }

    [Fact]
    public void BarLabels_KeepFullNamesWhereShortOnesWouldClash()
    {
        var labels = Status.BarLabels(
        [
            Limit(ProviderId.Antigravity, "Gemini 5h", 50),
            Limit(ProviderId.Antigravity, "Gemini 7d", 60),
            // Another account's "Gemini" is told apart by its badge, not its name.
            Limit(ProviderId.Antigravity, "Gemini 7d", 70, slot: "2", name: "Work"),
        ]);

        Assert.Equal(["Gemini 5h", "Gemini week…", "Gemini"], labels);
    }

    [Fact]
    public void RoundedRect_CoversThePanelAndCutsItsCorners()
    {
        var rows = Desktop.RoundedRect(30, 22, 400, 600, 18);

        // Every pixel inside, none outside: counted against the circle's own test.
        var covered = new HashSet<(int, int)>();
        foreach (var (x, y, w, h) in rows)
            for (var i = x; i < x + w; i++)
                for (var j = y; j < y + h; j++)
                    Assert.True(covered.Add((i, j)), $"({i},{j}) covered twice");
        Assert.DoesNotContain((30, 22), covered);         // the very corner is cut
        Assert.Contains((30 + 18, 22), covered);           // where the curve meets the top edge
        Assert.Contains((30, 22 + 18), covered);           // and the side
        Assert.Contains((229, 321), covered);              // the middle
        Assert.DoesNotContain((29, 300), covered);         // the shadow's room left of it
        Assert.DoesNotContain((430, 300), covered);        // and right of it
        Assert.All(covered, p => Assert.InRange(p.Item2, 22, 621));
        // Few rectangles, not one per row.
        Assert.InRange(rows.Count, 3, 2 * 18 + 1);
    }

    [Fact]
    public void BarMarkup_ColoursEachFigureByItsOwnLevel()
    {
        var markup = Status.BarMarkup(
        [
            new Status.BarPart("Kimi", "Weekly", 11),
            new Status.BarPart("Claude RZ", "5h", 38),
            new Status.BarPart("Antigravity", "Claude & GP…", 64),
        ], darkBar: true);

        Assert.Equal(
            "Kimi <span alpha=\"70%\">Weekly</span> <span color=\"#F59088\">11%</span>\u2003"
            + "Claude RZ <span alpha=\"70%\">5h</span> <span color=\"#F4CB7A\">38%</span>\u2003"
            + "Antigravity <span alpha=\"70%\">Claude &amp; GP…</span> 64%",
            markup);
    }

    [Fact]
    public void BarMarkup_PutsEachProvidersMarkWhereItsNameWouldBe()
    {
        var markup = Status.BarMarkup(
        [
            new Status.BarPart("Kimi", "Weekly", 11, ProviderId.Kimi),
            new Status.BarPart("Claude RZ", "5h", 73, ProviderId.Claude, "RZ"),
        ], darkBar: true, marks: true);

        Assert.Equal(
            "<span font_family=\"usagetop marks\">\uE003</span> <span alpha=\"70%\">Weekly</span> <span color=\"#F59088\">11%</span>\u2003"
            + "<span font_family=\"usagetop marks\">\uE000</span><span size=\"smaller\" weight=\"heavy\">RZ</span> <span alpha=\"70%\">5h</span> 73%",
            markup);
    }

    [Fact]
    public void BarLabels_Slim_NameAPeriodByItsLength()
    {
        Assert.Equal("7d", Status.Slim("Weekly"));
        Assert.Equal("1d", Status.Slim("Daily"));
        Assert.Equal("Gemini 7d", Status.Slim("Gemini weekly"));
        Assert.Equal("5h", Status.Slim("5h"));
        Assert.Equal("Fable", Status.Slim("Fable"));
        Assert.Equal("Vibe", Status.Slim("Vibe"));

        var labels = Status.BarLabels(
        [
            Limit(ProviderId.Antigravity, "Gemini 5h", 50),
            Limit(ProviderId.Antigravity, "Gemini 7d", 60),
            Limit(ProviderId.Kimi, "7d", 11),
        ], slim: true);
        Assert.Equal(["Gemini 5h", "Gemini 7d", "7d"], labels);
    }

    [Fact]
    public void Compact_NamesAWindowByItsModel()
    {
        Assert.Equal("Fable", WindowName.Compact("Fable 7d"));
        Assert.Equal("Weekly", WindowName.Compact("7d"));
        Assert.Equal("5h", WindowName.Compact("5h"));
        Assert.Equal("Vibe", WindowName.Compact("Vibe"));
    }
}
