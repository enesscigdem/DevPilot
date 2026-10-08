using DevPilot.Application.Tasks.Batch;
using DevPilot.Application.Tasks.Commands.CreateTask;
using DevPilot.Application.Tasks.Dtos;
using DevPilot.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests.Tasks;

public class TaskBatchTests
{
    private const string NotesBacklog = @"Task: Not Editöründe İçerik Alanının Responsive Hale Getirilmesi
Description: Not detay ekranında içerik alanı sabit/dar bir genişlikte kalıyor ve özellikle geniş ekranlarda sağ ve solda çok fazla boş alan oluşuyor.

Task: Boş Notların Otomatik Oluşmasının Engellenmesi
Description: Yeni not butonuna basıldığında kullanıcı herhangi bir içerik girmeden nottan çıkarsa boş kayıtlar kalmamalıdır.

Task: Not Silme İşlemine Geri Alma Desteği Eklenmesi
Description: Kullanıcı bir notu sildiğinde kısa süreli bir ""Not silindi – Geri Al"" bildirimi gösterilmelidir.

Task: Klavye Kısayollarının Eklenmesi
Description: Not kullanımını hızlandırmak için temel klavye kısayolları desteklenmelidir. Örneğin Ctrl+N yeni not, Ctrl+F not arama.";

    [Fact]
    public void Parses_task_and_description_blocks_in_order()
    {
        var result = TaskBatchParser.Parse(NotesBacklog);

        result.Warnings.Should().BeEmpty();
        result.Drafts.Select(d => d.Title).Should().Equal(
            "Not Editöründe İçerik Alanının Responsive Hale Getirilmesi",
            "Boş Notların Otomatik Oluşmasının Engellenmesi",
            "Not Silme İşlemine Geri Alma Desteği Eklenmesi",
            "Klavye Kısayollarının Eklenmesi");
        result.Drafts.Should().OnlyContain(d => d.Description.Length > 20);
        result.Drafts[2].Description.Should().Contain("Geri Al");
        result.Drafts.Should().OnlyContain(d => d.Priority == DevelopmentTaskPriority.Medium);
    }

    [Fact]
    public void Accepts_windows_line_endings()
    {
        var result = TaskBatchParser.Parse(NotesBacklog.Replace("\n", "\r\n"));

        result.Drafts.Should().HaveCount(4);
        result.Drafts[0].Description.Should().NotContain("\r");
    }

    [Fact]
    public void A_description_can_span_several_lines_and_paragraphs()
    {
        var result = TaskBatchParser.Parse("Task: A\nDescription: first line\nsecond line\n\nthird paragraph\nTask: B\nDescription: b");

        result.Drafts[0].Description.Should().Be("first line\nsecond line\n\nthird paragraph");
        result.Drafts[1].Description.Should().Be("b");
    }

    [Fact]
    public void Recognises_turkish_labels_bullets_and_bold_markers()
    {
        var result = TaskBatchParser.Parse(
            "- **Görev:** Arama düzeni\n  **Açıklama:** Başlık ve içerikte ara\n  **Öncelik:** Yüksek\n" +
            "- Görev: Toolbar\n  Kabul Kriterleri: Sticky olmalı\n");

        result.Drafts.Should().HaveCount(2);
        result.Drafts[0].Title.Should().Be("Arama düzeni");
        result.Drafts[0].Description.Should().Be("Başlık ve içerikte ara");
        result.Drafts[0].Priority.Should().Be(DevelopmentTaskPriority.High);
        result.Drafts[1].AcceptanceCriteria.Should().Be("Sticky olmalı");
    }

    [Theory]
    [InlineData("Low", DevelopmentTaskPriority.Low)]
    [InlineData("düşük", DevelopmentTaskPriority.Low)]
    [InlineData("Orta", DevelopmentTaskPriority.Medium)]
    [InlineData("HIGH", DevelopmentTaskPriority.High)]
    [InlineData("Kritik", DevelopmentTaskPriority.Critical)]
    public void Priority_words_are_understood(string text, DevelopmentTaskPriority expected)
    {
        TaskBatchParser.ParsePriority(text, out var recognised).Should().Be(expected);
        recognised.Should().BeTrue();
    }

    [Fact]
    public void An_unknown_priority_falls_back_to_medium_with_a_warning()
    {
        var result = TaskBatchParser.Parse("Task: A\nDescription: a\nPriority: whenever");

        result.Drafts.Single().Priority.Should().Be(DevelopmentTaskPriority.Medium);
        result.Warnings.Should().ContainSingle(w => w.Code == "UnknownPriority" && w.DraftIndex == 0);
    }

    [Fact]
    public void A_bulleted_or_numbered_list_becomes_titles()
    {
        var result = TaskBatchParser.Parse("Yapılacaklar:\n1. Arama ekle\n2) Toolbar sabit olsun\n- Silmeyi geri al\n  notu eski yerine koy");

        result.Drafts.Select(d => d.Title).Should().Equal("Arama ekle", "Toolbar sabit olsun", "Silmeyi geri al");
        result.Drafts[2].Description.Should().Be("notu eski yerine koy");
        result.Warnings.Should().Contain(w => w.Code == "TextBeforeFirstTask");
        result.Warnings.Should().Contain(w => w.Code == "MissingDescription" && w.DraftIndex == 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("Just a sentence without any structure.")]
    public void Text_without_tasks_yields_a_clear_warning(string text)
    {
        var result = TaskBatchParser.Parse(text);

        result.Drafts.Should().BeEmpty();
        result.Warnings.Should().Contain(w => w.Code == "NoTasksFound");
    }

    [Fact]
    public void Problems_are_reported_against_the_draft_the_person_sees()
    {
        var result = TaskBatchParser.Parse(
            $"Task: Same\nDescription: a\nTask: same\nDescription: b\nTask: {new string('x', 201)}\nDescription: c\nTask:\nDescription: no title");

        result.Warnings.Should().Contain(w => w.Code == "DuplicateTitle" && w.DraftIndex == 1);
        result.Warnings.Should().Contain(w => w.Code == "TitleTooLong" && w.DraftIndex == 2);
        result.Warnings.Should().Contain(w => w.Code == "MissingTitle" && w.DraftIndex == 3);
    }

    [Fact]
    public void A_description_before_any_task_is_not_attached_to_anything()
    {
        var result = TaskBatchParser.Parse("Description: orphan\nTask: A\nDescription: a");

        result.Drafts.Should().ContainSingle().Which.Description.Should().Be("a");
        result.Warnings.Should().Contain(w => w.Code == "TextBeforeFirstTask");
    }

    [Fact]
    public void Extra_tasks_beyond_the_limit_are_dropped_with_a_warning()
    {
        var text = string.Join("\n", Enumerable.Range(1, TaskBatchParser.MaxTasks + 5).Select(i => $"Task: T{i}\nDescription: d{i}"));

        var result = TaskBatchParser.Parse(text);

        result.Drafts.Should().HaveCount(TaskBatchParser.MaxTasks);
        result.Warnings.Should().Contain(w => w.Code == "TooManyTasks");
    }

    [Fact]
    public async Task One_failing_task_does_not_stop_the_others()
    {
        var create = new FakeCreateTask(title => title == "bad" ? "Title is required." : null);
        var handler = new CreateTaskBatchCommandHandler(create);

        var result = await handler.HandleAsync(new CreateTaskBatchCommand(
            Guid.NewGuid(),
            new[] { Item("a"), Item("bad"), Item("c") }));

        result.Success.Should().BeFalse();
        result.Items.Select(i => i.Success).Should().Equal(true, false, true);
        result.Items[1].ErrorMessage.Should().Be("Title is required.");
        create.Created.Should().Equal("a", "bad", "c");
    }

    [Fact]
    public async Task A_missing_workspace_stops_the_batch_after_the_first_task()
    {
        var create = new FakeCreateTask(_ => "Repository workspace not found.");
        var handler = new CreateTaskBatchCommandHandler(create);

        var result = await handler.HandleAsync(new CreateTaskBatchCommand(Guid.NewGuid(), new[] { Item("a"), Item("b") }));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Repository workspace not found.");
        create.Created.Should().ContainSingle();
    }

    [Fact]
    public async Task Empty_and_oversized_batches_are_rejected()
    {
        var handler = new CreateTaskBatchCommandHandler(new FakeCreateTask(_ => null));

        (await handler.HandleAsync(new CreateTaskBatchCommand(Guid.NewGuid(), Array.Empty<BatchTaskItem>())))
            .Success.Should().BeFalse();
        (await handler.HandleAsync(new CreateTaskBatchCommand(
                Guid.NewGuid(),
                Enumerable.Range(0, TaskBatchParser.MaxTasks + 1).Select(i => Item($"t{i}")).ToList())))
            .Success.Should().BeFalse();
    }

    private static BatchTaskItem Item(string title) => new() { Title = title, Description = "d" };

    private sealed class FakeCreateTask : ICreateTaskCommandHandler
    {
        private readonly Func<string, string?> _error;

        public FakeCreateTask(Func<string, string?> error) => _error = error;

        public List<string> Created { get; } = new();

        public Task<CreateTaskResult> HandleAsync(CreateTaskCommand command, CancellationToken cancellationToken = default)
        {
            Created.Add(command.Dto.Title);
            var error = _error(command.Dto.Title);
            return Task.FromResult(error is null
                ? new CreateTaskResult { Success = true, Task = new TaskDto { Id = Guid.NewGuid(), Title = command.Dto.Title } }
                : new CreateTaskResult { Success = false, ErrorMessage = error });
        }
    }
}
