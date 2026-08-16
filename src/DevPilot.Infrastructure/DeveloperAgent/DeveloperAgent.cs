using System.Text.Json;
using DevPilot.Application.AiProviders;
using DevPilot.Application.DeveloperAgent.Models;
using DevPilot.Application.DeveloperAgent.Ports;
using Microsoft.Extensions.Logging;

namespace DevPilot.Infrastructure.DeveloperAgent;

public sealed class DeveloperAgent : IDeveloperAgent
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IAiProvider _aiProvider;
    private readonly IWorktreeEditApplier _editApplier;
    private readonly ILogger<DeveloperAgent> _logger;

    public DeveloperAgent(
        IAiProvider aiProvider,
        IWorktreeEditApplier editApplier,
        ILogger<DeveloperAgent> logger)
    {
        _aiProvider = aiProvider;
        _editApplier = editApplier;
        _logger = logger;
    }

    public async Task<DeveloperAgentResult> GenerateAndApplyEditsAsync(
        DeveloperAgentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        _logger.LogInformation(
            "DeveloperAgent: starting edit generation for task {TaskId} in workspace '{Workspace}' on branch '{Branch}'.",
            request.TaskId,
            request.WorkspacePath,
            request.BranchName);

        // 1. Read context files safely within limits
        IReadOnlyDictionary<string, string> contextFiles;
        try
        {
            contextFiles = await _editApplier.ReadContextFilesAsync(
                request.WorkspacePath,
                request.BranchName,
                request.ImpactedFilePaths,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeveloperAgent: failed to read context files for task {TaskId}.", request.TaskId);
            return DeveloperAgentResult.Fail($"Context loading failed: {ex.Message}");
        }

        // 2. Build prompt for AI provider
        var systemPrompt = BuildSystemPrompt();
        var userPrompt = BuildUserPrompt(request, contextFiles);

        var aiRequest = new AiRequest
        {
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt
        };

        // 3. Invoke AI Provider
        AiResponse aiResponse;
        try
        {
            aiResponse = await _aiProvider.SendAsync(aiRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeveloperAgent: AI provider call failed for task {TaskId}.", request.TaskId);
            return DeveloperAgentResult.Fail($"AI provider call failed: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(aiResponse.Content))
        {
            return DeveloperAgentResult.Fail("AI provider returned empty response.");
        }

        // 4. Parse structured JSON response
        StructuredEditPlan editPlan;
        try
        {
            editPlan = ParseStructuredPlan(aiResponse.Content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeveloperAgent: failed to parse structured JSON response from AI provider.");
            return DeveloperAgentResult.Fail($"Failed to parse AI response into structured edit plan: {ex.Message}");
        }

        // 5. Apply edits safely
        return await _editApplier.ApplyEditsAsync(
            request.WorkspacePath,
            request.BranchName,
            editPlan,
            cancellationToken).ConfigureAwait(false);
    }

    private static string BuildSystemPrompt()
    {
        return """
            You are a software developer agent. Your goal is to produce constrained, exact file edits to implement the requested development task.

            CRITICAL RULES:
            1. Respond ONLY with a valid JSON object adhering to the schema below. Do not include markdown code fence formatting outside the JSON object or any conversational text.
            2. Do NOT attempt file deletion or rename.
            3. Do NOT execute arbitrary shell commands.
            4. For 'Modify' actions, each search string must match EXACTLY ONCE in the target file.
            5. For 'Create' actions, specify 'newContent' explicitly.

            JSON Schema:
            {
              "files": [
                {
                  "filePath": "relative/path/to/file.ext",
                  "action": "Modify",
                  "searchReplaceEdits": [
                    {
                      "search": "exact code to find",
                      "replace": "exact code to replace with"
                    }
                  ]
                },
                {
                  "filePath": "relative/path/to/newfile.ext",
                  "action": "Create",
                  "newContent": "file contents"
                }
              ]
            }
            """;
    }

    private static string BuildUserPrompt(DeveloperAgentRequest request, IReadOnlyDictionary<string, string> contextFiles)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Task Title: {request.TaskTitle}");
        sb.AppendLine($"Task Description: {request.TaskDescription}");
        if (!string.IsNullOrWhiteSpace(request.AcceptanceCriteria))
        {
            sb.AppendLine($"Acceptance Criteria: {request.AcceptanceCriteria}");
        }
        sb.AppendLine();
        sb.AppendLine("=== Impact Analysis Summary ===");
        sb.AppendLine(request.ImpactAnalysisSummary);
        sb.AppendLine();
        sb.AppendLine("=== Proposed Plan ===");
        sb.AppendLine(request.ProposedPlan);
        sb.AppendLine();
        sb.AppendLine("=== Relevant Source Files ===");

        foreach (var (path, content) in contextFiles)
        {
            sb.AppendLine($"--- File: {path} ---");
            sb.AppendLine(content);
            sb.AppendLine("--- End File ---");
        }

        return sb.ToString();
    }

    public static StructuredEditPlan ParseStructuredPlan(string rawContent)
    {
        var cleaned = rawContent.Trim();
        if (cleaned.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned.Substring(7);
        }
        else if (cleaned.StartsWith("```"))
        {
            cleaned = cleaned.Substring(3);
        }

        if (cleaned.EndsWith("```"))
        {
            cleaned = cleaned.Substring(0, cleaned.Length - 3);
        }

        cleaned = cleaned.Trim();

        var plan = JsonSerializer.Deserialize<StructuredEditPlan>(cleaned, JsonOpts);
        if (plan == null)
        {
            throw new InvalidOperationException("Deserialized edit plan is null.");
        }

        return plan;
    }
}
