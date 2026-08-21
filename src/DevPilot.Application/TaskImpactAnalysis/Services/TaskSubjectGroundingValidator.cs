using System.Text.RegularExpressions;
using DevPilot.Application.ProjectBrain.Ports;
using DevPilot.Domain.ProjectBrain;

namespace DevPilot.Application.TaskImpactAnalysis.Services;

public sealed class TaskSubjectGroundingResult
{
    public bool IsGrounded { get; set; } = true;

    public string? TargetEntity { get; set; }

    public string? TargetProperty { get; set; }

    public string? TargetSubject { get; set; }

    public string? UnresolvedReason { get; set; }

    public bool IsEntityMissing { get; set; }

    public bool IsPropertyMissing { get; set; }

    public static TaskSubjectGroundingResult Success(string? entity = null, string? property = null) =>
        new()
        {
            IsGrounded = true,
            TargetEntity = entity,
            TargetProperty = property,
            TargetSubject = !string.IsNullOrWhiteSpace(entity) && !string.IsNullOrWhiteSpace(property)
                ? $"{entity}.{property}"
                : entity ?? property
        };

    public static TaskSubjectGroundingResult Unresolved(
        string subject,
        string reason,
        string? entity = null,
        string? property = null,
        bool isEntityMissing = false,
        bool isPropertyMissing = false) =>
        new()
        {
            IsGrounded = false,
            TargetSubject = subject,
            TargetEntity = entity,
            TargetProperty = property,
            UnresolvedReason = reason,
            IsEntityMissing = isEntityMissing,
            IsPropertyMissing = isPropertyMissing
        };
}

public static class TaskSubjectGroundingValidator
{
    // Well-known .NET BCL, ASP.NET Core, EF Core, and common library framework types
    private static readonly HashSet<string> KnownFrameworkTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Configuration", "IConfiguration", "ConfigurationBuilder", "IConfigurationBuilder",
        "WebApplication", "WebApplicationFactory", "CustomWebApplicationFactory", "Program", "Startup",
        "Services", "IServiceCollection", "IServiceProvider", "ServiceCollection",
        "DbContext", "DbContextOptions", "DbContextOptionsBuilder", "ModelBuilder", "MigrationBuilder",
        "Host", "HostBuilder", "IHostBuilder", "IApplicationBuilder", "IEndpointRouteBuilder", "EndpointRouteBuilder",
        "ILogger", "Logger", "LoggerFactory", "ILoggerFactory", "Console", "Debug", "Trace",
        "Task", "ValueTask", "Thread", "ThreadPool",
        "String", "Int32", "Int64", "Guid", "DateTime", "DateTimeOffset", "TimeSpan", "Decimal", "Boolean",
        "Math", "Convert", "File", "Directory", "Path", "Stream", "MemoryStream",
        "HttpClient", "HttpRequest", "HttpResponse", "HttpContext", "ClaimsPrincipal", "ClaimsIdentity",
        "JwtBearer", "Authentication", "Authorization", "AuthenticationBuilder", "AuthorizationBuilder",
        "Options", "IOptions", "IOptionsSnapshot", "IOptionsMonitor",
        "IMapper", "Mapper", "AutoMapper", "MediatR", "ISender", "IMediator", "IPublisher",
        "FluentValidation", "AbstractValidator", "ValidationResult",
        "Xunit", "Fact", "Theory", "Assert", "Should", "Moq", "Mock", "AutoFixture", "Bogus", "Faker",
        "Redis", "StackExchange", "Newtonsoft", "JsonSerializer", "JsonDocument", "JsonElement", "Regex",
        "Environment", "AppDomain", "Assembly", "Type", "Activator",
        "Enumerable", "Queryable", "List", "IList", "Dictionary", "IDictionary", "HashSet", "ISet",
        "Array", "Span", "ReadOnlySpan", "Memory", "ReadOnlyMemory",
        "CancellationToken", "CancellationTokenSource",
        "Exception", "InvalidOperationException", "ArgumentNullException", "ArgumentException",
        "StatusCode", "StatusCodes", "Results", "TypedResults", "IResult", "IActionResult", "ActionResult",
        "ControllerBase", "Controller", "ApiExplorer", "Swagger", "OpenApi"
    };

    // Well-known framework methods and extension methods
    private static readonly HashSet<string> KnownFrameworkMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GetConnectionString", "GetSection", "GetValue",
        "AddDbContext", "AddDbContextFactory", "AddDbContextPool",
        "UseAuthentication", "UseAuthorization", "UseHttpsRedirection", "UseRouting", "UseEndpoints",
        "MapControllers", "MapGet", "MapPost", "MapPut", "MapDelete", "MapGroup",
        "AddControllers", "AddEndpointsApiExplorer", "AddSwaggerGen",
        "AddScoped", "AddTransient", "AddSingleton", "AddHttpClient", "AddLogging", "AddOptions",
        "AddMediatR", "AddAutoMapper", "AddValidatorsFromAssembly", "AddValidatorsFromAssemblyContaining",
        "CreateBuilder", "Build", "Run", "RunAsync", "Configure", "ConfigureServices",
        "ConfigureWebHostDefaults", "CreateDefaultBuilder", "CreateClient", "WithWebHostBuilder", "ConfigureTestServices",
        "Migrate", "EnsureCreated", "EnsureDeleted", "Database", "SaveChangesAsync",
        "ToListAsync", "FirstOrDefaultAsync", "SingleOrDefaultAsync", "AnyAsync", "CountAsync",
        "Select", "Where", "OrderBy", "OrderByDescending", "GroupBy", "Join", "Include", "ThenInclude",
        "AsNoTracking", "AsQueryable", "Add", "AddRange", "Remove", "RemoveRange", "Update", "UpdateRange",
        "Find", "FindAsync", "Contains", "ToString", "Equals", "GetHashCode", "GetType"
    };

    // Words from prose/punctuation that should never be treated as C# types
    private static readonly HashSet<string> IgnoredProseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "her", "bir", "ve", "veya", "ile", "için", "gibi", "olan", "the", "this", "that", "with", "from", "for", "and", "or", "in", "on", "at", "to"
    };

    public static TaskSubjectGroundingResult Validate(
        string? taskPrompt,
        RepositoryEvidenceProfile evidence,
        string? workspaceLocalPath)
    {
        if (string.IsNullOrWhiteSpace(taskPrompt))
        {
            return TaskSubjectGroundingResult.Success();
        }

        var candidate = ExtractExplicitSubject(taskPrompt);

        if (candidate == null || !candidate.IsCentralRepositorySubject)
        {
            // No central repository-owned subject identified; do NOT block execution
            return TaskSubjectGroundingResult.Success();
        }

        var entity = candidate.Entity;
        var property = candidate.Property;
        var isModifyingExisting = candidate.IsModifyingExisting;

        if (string.IsNullOrWhiteSpace(entity))
        {
            return TaskSubjectGroundingResult.Success();
        }

        // 1. Check if the named entity/service/controller exists in repository evidence
        var entityFound = IsEntityInRepository(entity, evidence, workspaceLocalPath, out var entityFilePath);

        if (!entityFound)
        {
            var subjectName = !string.IsNullOrWhiteSpace(property) ? $"{entity}.{property}" : entity;
            var reason = $"{subjectName} could not be resolved in repository evidence.";
            return TaskSubjectGroundingResult.Unresolved(
                subject: subjectName,
                reason: reason,
                entity: entity,
                property: property,
                isEntityMissing: true);
        }

        // 2. If entity exists, check if an explicitly modified existing member/property exists
        if (isModifyingExisting && !string.IsNullOrWhiteSpace(property) && !string.IsNullOrWhiteSpace(entityFilePath))
        {
            var memberFound = IsMemberInEntityFile(entityFilePath, property, workspaceLocalPath);
            if (!memberFound)
            {
                var subjectName = $"{entity}.{property}";
                var reason = $"{subjectName} could not be resolved in repository evidence.";
                return TaskSubjectGroundingResult.Unresolved(
                    subject: subjectName,
                    reason: reason,
                    entity: entity,
                    property: property,
                    isPropertyMissing: true);
            }
        }

        return TaskSubjectGroundingResult.Success(entity, property);
    }

    private sealed class SubjectCandidate
    {
        public string Entity { get; init; } = string.Empty;
        public string? Property { get; init; }
        public bool IsModifyingExisting { get; init; }
        public bool IsCentralRepositorySubject { get; init; }
    }

    private static SubjectCandidate? ExtractExplicitSubject(string prompt)
    {
        var p = prompt.Trim();

        // 1. Explicit Turkish repository role indicators (highest precision)
        // Example: "Customer entity’sindeki Email alanını zorunlu hale getirelim..."
        // Example: "OrderService servisindeki CalculateTotal metodunu değiştir..."
        // Example: "OrdersController'daki List endpoint'ini güncelle..."
        // Example: "Customer tablosundaki Email alanını..."
        var trEntityPropMatch = Regex.Match(
            p,
            @"\b([A-Z][a-zA-Z0-9_]{1,60})\s*(?:entity['’]sindeki|entity['’]si|entity['’]deki|varlığı|tablosu|sınıfı|modeli|servisi|servisindeki|controller['’]ı|controller['’]ındaki|repository['’]si|['’]deki|['’]daki|['’]teki|['’]taki)\s+([A-Z][a-zA-Z0-9_]{1,60})\s*(?:alanı|alanını|metodu|metodunu|endpoint['’]i|endpoint['’]ini|property|özelliği|kolonu|sütunu)?",
            RegexOptions.IgnoreCase);

        if (trEntityPropMatch.Success)
        {
            var entity = trEntityPropMatch.Groups[1].Value;
            var property = trEntityPropMatch.Groups[2].Value;

            if (IsValidRepositoryIdentifier(entity) && IsValidRepositoryIdentifier(property) && !IsFrameworkTypeOrMethod(entity, property))
            {
                return new SubjectCandidate
                {
                    Entity = entity,
                    Property = property,
                    IsModifyingExisting = IsModifyingIntent(p),
                    IsCentralRepositorySubject = true
                };
            }
        }

        // 2. Explicit English repository role indicators (highest precision)
        // Example: "Make Customer entity Email field required"
        // Example: "Modify OrderService.CalculateTotal method"
        // Example: "Update OrdersController List endpoint"
        var enExplicitMatch = Regex.Match(
            p,
            @"(?:make|update|modify|change|reduce)\s+(?:the\s+)?([A-Z][a-zA-Z0-9_]{1,60})\s+(?:entity|model|class|service|controller|repository|table)\s+(?:(?:to\s+)?make\s+)?([A-Z][a-zA-Z0-9_]{1,60})\s*(?:field|property|method|endpoint|column)?",
            RegexOptions.IgnoreCase);

        if (enExplicitMatch.Success)
        {
            var entity = enExplicitMatch.Groups[1].Value;
            var property = enExplicitMatch.Groups[2].Value;

            if (IsValidRepositoryIdentifier(entity) && IsValidRepositoryIdentifier(property) && !IsFrameworkTypeOrMethod(entity, property))
            {
                return new SubjectCandidate
                {
                    Entity = entity,
                    Property = property,
                    IsModifyingExisting = true,
                    IsCentralRepositorySubject = true
                };
            }
        }

        // 3. English Add Property to Entity
        // Example: "Add optional DiscountAmount to Order entity"
        var enAddMatch = Regex.Match(
            p,
            @"add\s+(?:an? )?(?:optional |nullable |required |non-nullable )?([A-Z][a-zA-Z0-9_]{1,60})(?: field| property| column)?\s+to\s+(?:the\s+)?([A-Z][a-zA-Z0-9_]{1,60})\s+(?:entity|table|model|class)",
            RegexOptions.IgnoreCase);

        if (enAddMatch.Success)
        {
            var property = enAddMatch.Groups[1].Value;
            var entity = enAddMatch.Groups[2].Value;

            if (IsValidRepositoryIdentifier(entity) && IsValidRepositoryIdentifier(property) && !IsFrameworkTypeOrMethod(entity, property))
            {
                return new SubjectCandidate
                {
                    Entity = entity,
                    Property = property,
                    IsModifyingExisting = false,
                    IsCentralRepositorySubject = true
                };
            }
        }

        // 4. Dot notation WITH explicit action intent
        // Example: "OrderService.CalculateTotal metodunu değiştir" / "Make Customer.Email required"
        var dotActionMatch = Regex.Match(
            p,
            @"(?:make|update|modify|change|reduce|değiştir|güncelle|zorunlu|yap)\s+(?:the\s+)?([A-Z][a-zA-Z0-9_]{1,60})\.([A-Z][a-zA-Z0-9_]{1,60})\b",
            RegexOptions.IgnoreCase);

        if (dotActionMatch.Success)
        {
            var entity = dotActionMatch.Groups[1].Value;
            var property = dotActionMatch.Groups[2].Value;

            if (IsValidRepositoryIdentifier(entity) && IsValidRepositoryIdentifier(property) && !IsFrameworkTypeOrMethod(entity, property))
            {
                return new SubjectCandidate
                {
                    Entity = entity,
                    Property = property,
                    IsModifyingExisting = IsModifyingIntent(p),
                    IsCentralRepositorySubject = true
                };
            }
        }

        // 5. Entity-only mention with explicit role keyword (e.g. "Customer entity", "OrderService", "OrdersController")
        var entityOnlyMatch = Regex.Match(
            p,
            @"\b([A-Z][a-zA-Z0-9_]{1,60})\s+(?:entity|model|service|controller|repository)\b",
            RegexOptions.IgnoreCase);

        if (entityOnlyMatch.Success)
        {
            var entity = entityOnlyMatch.Groups[1].Value;
            if (IsValidRepositoryIdentifier(entity) && !KnownFrameworkTypes.Contains(entity))
            {
                // Check if property is also mentioned
                string? property = null;
                var propMatch = Regex.Match(p, @"\b([A-Z][a-zA-Z0-9_]{1,60})\s+(?:field|property|alanı|alanını|column|sütunu|metodu|endpoint['’]i)\b", RegexOptions.IgnoreCase);
                if (propMatch.Success && !string.Equals(propMatch.Groups[1].Value, entity, StringComparison.OrdinalIgnoreCase))
                {
                    var candProp = propMatch.Groups[1].Value;
                    if (IsValidRepositoryIdentifier(candProp) && !KnownFrameworkMethods.Contains(candProp))
                    {
                        property = candProp;
                    }
                }

                return new SubjectCandidate
                {
                    Entity = entity,
                    Property = property,
                    IsModifyingExisting = IsModifyingIntent(p),
                    IsCentralRepositorySubject = true
                };
            }
        }

        return null;
    }

    private static bool IsValidRepositoryIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Length < 2)
        {
            return false;
        }

        // Must be ASCII PascalCase identifier (starts with uppercase A-Z, only alphanumeric / underscore)
        if (!char.IsAsciiLetterUpper(identifier[0]))
        {
            return false;
        }

        if (!identifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            return false;
        }

        if (IgnoredProseWords.Contains(identifier))
        {
            return false;
        }

        return true;
    }

    private static bool IsFrameworkTypeOrMethod(string entity, string? methodOrProperty)
    {
        if (KnownFrameworkTypes.Contains(entity))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(methodOrProperty) && KnownFrameworkMethods.Contains(methodOrProperty))
        {
            return true;
        }

        return false;
    }

    private static bool IsModifyingIntent(string prompt)
    {
        var p = prompt.ToLowerInvariant();
        return p.Contains("zorunlu") ||
               p.Contains("düşür") ||
               p.Contains("artır") ||
               p.Contains("değiştir") ||
               p.Contains("güncelle") ||
               p.Contains("required") ||
               p.Contains("reduce") ||
               p.Contains("modify") ||
               p.Contains("update") ||
               p.Contains("alter") ||
               p.Contains("rename");
    }

    private static bool IsEntityInRepository(
        string entityName,
        RepositoryEvidenceProfile evidence,
        string? workspaceLocalPath,
        out string? matchedFilePath)
    {
        matchedFilePath = null;

        // 1. Check inventory files
        foreach (var file in evidence.InventoryCsFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(fileName, entityName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, $"{entityName}Entity", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, $"{entityName}Model", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, $"{entityName}Service", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, $"{entityName}Controller", StringComparison.OrdinalIgnoreCase))
            {
                matchedFilePath = file;
                return true;
            }
        }

        // 2. Check persistence files
        foreach (var file in evidence.PersistenceFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(fileName, entityName, StringComparison.OrdinalIgnoreCase))
            {
                matchedFilePath = file;
                return true;
            }
        }

        // 3. Check controller files
        foreach (var file in evidence.ControllerFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(fileName, $"{entityName}Controller", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, entityName, StringComparison.OrdinalIgnoreCase))
            {
                matchedFilePath = file;
                return true;
            }
        }

        // 4. If local workspace is available, check file contents for class/record/interface declaration
        if (!string.IsNullOrWhiteSpace(workspaceLocalPath) && Directory.Exists(workspaceLocalPath))
        {
            try
            {
                var files = Directory.GetFiles(workspaceLocalPath, "*.cs", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                        file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
                    {
                        continue;
                    }

                    var fileName = Path.GetFileNameWithoutExtension(file);
                    if (string.Equals(fileName, entityName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedFilePath = file;
                        return true;
                    }

                    // Check content for class definition
                    var content = File.ReadAllText(file);
                    if (Regex.IsMatch(content, $@"\b(?:class|record|struct|interface|enum)\s+{Regex.Escape(entityName)}\b"))
                    {
                        matchedFilePath = file;
                        return true;
                    }
                }
            }
            catch
            {
                // Fall back to evidence
            }
        }

        return false;
    }

    private static bool IsMemberInEntityFile(
        string entityFilePath,
        string memberName,
        string? workspaceLocalPath)
    {
        string? content = null;

        if (!string.IsNullOrWhiteSpace(workspaceLocalPath) && Directory.Exists(workspaceLocalPath))
        {
            var fullPath = Path.IsPathRooted(entityFilePath)
                ? entityFilePath
                : Path.Combine(workspaceLocalPath, entityFilePath.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(fullPath))
            {
                try { content = File.ReadAllText(fullPath); } catch { /* Ignore */ }
            }
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            // If file cannot be read directly, assume grounded to avoid false positives on unreadable files
            return true;
        }

        // Check if property, method, or field is defined in file
        return Regex.IsMatch(content, $@"\b{Regex.Escape(memberName)}\b\s*\{{\s*(?:get|set|init)", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(content, $@"public\s+[\w<>?,\[\]\s]+\s+{Regex.Escape(memberName)}\b", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(content, $@"\b{Regex.Escape(memberName)}\s*\(", RegexOptions.IgnoreCase);
    }
}
