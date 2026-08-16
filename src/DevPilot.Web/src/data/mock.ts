/* ============================================================================
   DevPilot mock data
   A realistic slice of a .NET (Clean Architecture) + React repository as it
   would be seen by DevPilot's Roslyn-based analyzer.
   ========================================================================== */

export type Tone = "neutral" | "blue" | "amber" | "green" | "red" | "gray"

export type TaskStatus =
  | "awaiting-approval"
  | "executing"
  | "blocked"
  | "failed"
  | "done"
  | "planning"
  | "draft"

export const statusMeta: Record<TaskStatus, { label: string; tone: Tone }> = {
  "awaiting-approval": { label: "Awaiting approval", tone: "amber" },
  executing: { label: "Executing", tone: "blue" },
  blocked: { label: "Blocked", tone: "red" },
  failed: { label: "Failed", tone: "red" },
  done: { label: "Merged", tone: "green" },
  planning: { label: "Planning", tone: "neutral" },
  draft: { label: "Draft", tone: "gray" },
}

export type RiskLevel = "low" | "medium" | "high"
export const riskMeta: Record<RiskLevel, { label: string; tone: Tone }> = {
  low: { label: "Low risk", tone: "green" },
  medium: { label: "Medium risk", tone: "amber" },
  high: { label: "High risk", tone: "red" },
}

/* ------------------------------- Repository -------------------------------- */

export const repository = {
  owner: "enesscigdem",
  name: "DevPilot",
  fullName: "enesscigdem/DevPilot",
  branch: "master",
  visibility: "private" as const,
  defaultBranch: "master",
  lastIndexed: "6 minutes ago",
  commit: "a1f9c72",
  commitMessage: "refactor: extract order pricing into domain service",
  loc: 48213,
  files: 612,
  language: "C# · TypeScript",
}

export const indexer = {
  state: "ready" as "ready" | "indexing" | "stale",
  progress: 100,
  symbols: 8421,
  types: 1140,
  references: 26308,
  lastRun: "6 minutes ago",
  engine: "Roslyn workspace analysis",
  steps: [
    { label: "Clone & restore", done: true },
    { label: "Compile solution", done: true },
    { label: "Build symbol graph", done: true },
    { label: "Resolve references", done: true },
    { label: "Index embeddings", done: true },
  ],
}

export const technologies = [
  { name: ".NET", version: "8.0", kind: "runtime", tone: "blue" as Tone },
  { name: "ASP.NET Core", version: "8.0", kind: "framework", tone: "blue" as Tone },
  { name: "Entity Framework Core", version: "8.0.4", kind: "orm", tone: "neutral" as Tone },
  { name: "PostgreSQL", version: "16", kind: "database", tone: "neutral" as Tone },
  { name: "MediatR", version: "12.2", kind: "library", tone: "neutral" as Tone },
  { name: "FluentValidation", version: "11.9", kind: "library", tone: "neutral" as Tone },
  { name: "React", version: "18.3", kind: "frontend", tone: "blue" as Tone },
  { name: "TypeScript", version: "5.4", kind: "frontend", tone: "blue" as Tone },
  { name: "xUnit", version: "2.7", kind: "testing", tone: "neutral" as Tone },
]

export const projects = [
  { name: "DevPilot.Api", kind: "Web API", files: 63, layer: "Web", tone: "blue" as Tone },
  { name: "DevPilot.Application", kind: "Class Library", files: 148, layer: "Application", tone: "amber" as Tone },
  { name: "DevPilot.Domain", kind: "Class Library", files: 92, layer: "Domain", tone: "green" as Tone },
  { name: "DevPilot.Infrastructure", kind: "Class Library", files: 111, layer: "Infrastructure", tone: "neutral" as Tone },
  { name: "DevPilot.Web", kind: "React SPA", files: 174, layer: "Web", tone: "blue" as Tone },
  { name: "DevPilot.Tests", kind: "xUnit", files: 24, layer: "Tests", tone: "gray" as Tone },
]

export interface Endpoint {
  method: "GET" | "POST" | "PUT" | "DELETE" | "PATCH"
  route: string
  controller: string
  action: string
  auth: boolean
}

export const endpoints: Endpoint[] = [
  { method: "GET", route: "/api/orders", controller: "OrdersController", action: "GetOrders", auth: true },
  { method: "GET", route: "/api/orders/{id}", controller: "OrdersController", action: "GetOrderById", auth: true },
  { method: "POST", route: "/api/orders", controller: "OrdersController", action: "CreateOrder", auth: true },
  { method: "PUT", route: "/api/orders/{id}/status", controller: "OrdersController", action: "UpdateStatus", auth: true },
  { method: "GET", route: "/api/products", controller: "ProductsController", action: "GetProducts", auth: false },
  { method: "POST", route: "/api/auth/login", controller: "AuthController", action: "Login", auth: false },
  { method: "POST", route: "/api/auth/refresh", controller: "AuthController", action: "Refresh", auth: false },
  { method: "GET", route: "/api/customers/{id}", controller: "CustomersController", action: "GetCustomer", auth: true },
]

/* ------------------------------- File tree --------------------------------- */

export interface FileNode {
  name: string
  path: string
  type: "folder" | "file"
  lang?: "cs" | "ts" | "tsx" | "json" | "sql" | "md"
  children?: FileNode[]
}

export const fileTree: FileNode[] = [
  {
    name: "src",
    path: "src",
    type: "folder",
    children: [
      {
        name: "DevPilot.Api",
        path: "src/DevPilot.Api",
        type: "folder",
        children: [
          {
            name: "Controllers",
            path: "src/DevPilot.Api/Controllers",
            type: "folder",
            children: [
              { name: "OrdersController.cs", path: "src/DevPilot.Api/Controllers/OrdersController.cs", type: "file", lang: "cs" },
              { name: "ProductsController.cs", path: "src/DevPilot.Api/Controllers/ProductsController.cs", type: "file", lang: "cs" },
              { name: "AuthController.cs", path: "src/DevPilot.Api/Controllers/AuthController.cs", type: "file", lang: "cs" },
            ],
          },
          { name: "Program.cs", path: "src/DevPilot.Api/Program.cs", type: "file", lang: "cs" },
        ],
      },
      {
        name: "DevPilot.Application",
        path: "src/DevPilot.Application",
        type: "folder",
        children: [
          {
            name: "Orders",
            path: "src/DevPilot.Application/Orders",
            type: "folder",
            children: [
              { name: "OrderService.cs", path: "src/DevPilot.Application/Orders/OrderService.cs", type: "file", lang: "cs" },
              { name: "GetOrdersQuery.cs", path: "src/DevPilot.Application/Orders/GetOrdersQuery.cs", type: "file", lang: "cs" },
              { name: "OrderFilter.cs", path: "src/DevPilot.Application/Orders/OrderFilter.cs", type: "file", lang: "cs" },
            ],
          },
        ],
      },
      {
        name: "DevPilot.Domain",
        path: "src/DevPilot.Domain",
        type: "folder",
        children: [
          {
            name: "Orders",
            path: "src/DevPilot.Domain/Orders",
            type: "folder",
            children: [
              { name: "Order.cs", path: "src/DevPilot.Domain/Orders/Order.cs", type: "file", lang: "cs" },
              { name: "OrderStatus.cs", path: "src/DevPilot.Domain/Orders/OrderStatus.cs", type: "file", lang: "cs" },
              { name: "IOrderRepository.cs", path: "src/DevPilot.Domain/Orders/IOrderRepository.cs", type: "file", lang: "cs" },
            ],
          },
        ],
      },
      {
        name: "DevPilot.Infrastructure",
        path: "src/DevPilot.Infrastructure",
        type: "folder",
        children: [
          {
            name: "Persistence",
            path: "src/DevPilot.Infrastructure/Persistence",
            type: "folder",
            children: [
              { name: "OrderRepository.cs", path: "src/DevPilot.Infrastructure/Persistence/OrderRepository.cs", type: "file", lang: "cs" },
              { name: "AppDbContext.cs", path: "src/DevPilot.Infrastructure/Persistence/AppDbContext.cs", type: "file", lang: "cs" },
            ],
          },
          {
            name: "Auth",
            path: "src/DevPilot.Infrastructure/Auth",
            type: "folder",
            children: [
              { name: "AuthService.cs", path: "src/DevPilot.Infrastructure/Auth/AuthService.cs", type: "file", lang: "cs" },
              { name: "JwtTokenService.cs", path: "src/DevPilot.Infrastructure/Auth/JwtTokenService.cs", type: "file", lang: "cs" },
            ],
          },
        ],
      },
      {
        name: "DevPilot.Web",
        path: "src/DevPilot.Web",
        type: "folder",
        children: [
          {
            name: "pages",
            path: "src/DevPilot.Web/pages",
            type: "folder",
            children: [
              { name: "OrdersPage.tsx", path: "src/DevPilot.Web/pages/OrdersPage.tsx", type: "file", lang: "tsx" },
            ],
          },
          {
            name: "api",
            path: "src/DevPilot.Web/api",
            type: "folder",
            children: [{ name: "ordersApi.ts", path: "src/DevPilot.Web/api/ordersApi.ts", type: "file", lang: "ts" }],
          },
        ],
      },
    ],
  },
]

/* ------------------------------- Attention --------------------------------- */

export interface AttentionItem {
  id: string
  title: string
  reason: string
  tone: Tone
  cta: string
  href: string
  meta: string
}

export const attention: AttentionItem[] = [
  {
    id: "att-1",
    title: "Plan ready for review",
    reason: "TASK-142 · Add status filtering to the orders endpoint",
    tone: "amber",
    cta: "Review plan",
    href: "/tasks/TASK-142",
    meta: "5 files · medium risk",
  },
  {
    id: "att-2",
    title: "Build failed during execution",
    reason: "TASK-138 · Migrate customer notifications to outbox pattern",
    tone: "red",
    cta: "Inspect failure",
    href: "/executions/EXEC-138",
    meta: "step 4/6 · Build & Test",
  },
  {
    id: "att-3",
    title: "Reviewer flagged a risk",
    reason: "TASK-140 · Cache product catalog responses",
    tone: "amber",
    cta: "Open review",
    href: "/review/TASK-140",
    meta: "1 blocking · 2 notes",
  },
]

/* --------------------------------- Tasks ----------------------------------- */

export interface Task {
  id: string
  title: string
  status: TaskStatus
  risk: RiskLevel
  confidence: number
  branch: string
  author: string
  updated: string
  filesTouched: number
  summary: string
}

export const tasks: Task[] = [
  {
    id: "TASK-142",
    title: "Add status filtering to the orders endpoint",
    status: "awaiting-approval",
    risk: "medium",
    confidence: 92,
    branch: "feat/orders-status-filter",
    author: "You",
    updated: "5 min ago",
    filesTouched: 5,
    summary: "Extend GET /api/orders with an optional status query parameter and propagate it through the query pipeline.",
  },
  {
    id: "TASK-141",
    title: "Add pagination metadata to product listing",
    status: "executing",
    risk: "low",
    confidence: 88,
    branch: "feat/product-pagination",
    author: "You",
    updated: "just now",
    filesTouched: 4,
    summary: "Return total count and page metadata from the products endpoint for the catalog grid.",
  },
  {
    id: "TASK-140",
    title: "Cache product catalog responses",
    status: "blocked",
    risk: "medium",
    confidence: 71,
    branch: "perf/product-cache",
    author: "M. Yılmaz",
    updated: "40 min ago",
    filesTouched: 6,
    summary: "Introduce distributed caching for product reads; reviewer flagged cache invalidation risk.",
  },
  {
    id: "TASK-139",
    title: "Enforce role claims on customer endpoints",
    status: "done",
    risk: "low",
    confidence: 96,
    branch: "sec/customer-role-claims",
    author: "You",
    updated: "2 hours ago",
    filesTouched: 3,
    summary: "Require Admin/Support role for customer detail access; merged to master.",
  },
  {
    id: "TASK-138",
    title: "Migrate customer notifications to outbox pattern",
    status: "failed",
    risk: "high",
    confidence: 64,
    branch: "refactor/notification-outbox",
    author: "A. Demir",
    updated: "1 hour ago",
    filesTouched: 9,
    summary: "Move notification dispatch to a transactional outbox; build failed on a missing migration.",
  },
  {
    id: "TASK-137",
    title: "Draft: split OrderService pricing logic",
    status: "draft",
    risk: "medium",
    confidence: 0,
    branch: "—",
    author: "You",
    updated: "yesterday",
    filesTouched: 0,
    summary: "Rough idea to extract pricing into a dedicated domain service. Not yet analyzed.",
  },
]

/* --------------------------- Signature task detail ------------------------- */

export const activeTask = {
  id: "TASK-142",
  title: "Add status filtering to the orders endpoint",
  status: "awaiting-approval" as TaskStatus,
  risk: "medium" as RiskLevel,
  confidence: 92,
  branch: "feat/orders-status-filter",
  requirement:
    "The orders API should support filtering by order status so the operations team can quickly triage open, shipped and cancelled orders.",
  acceptance: [
    "GET /api/orders accepts an optional `status` query parameter",
    "Invalid status values return 400 with a descriptive error",
    "Existing calls with no status parameter behave exactly as before",
    "Filtering is applied at the database layer, not in memory",
    "New behavior is covered by unit and integration tests",
  ],
  planSteps: [
    {
      title: "Extend the query contract",
      detail: "Add a nullable `OrderStatus? Status` to GetOrdersQuery and OrderFilter, mapped from the query string.",
      files: ["GetOrdersQuery.cs", "OrderFilter.cs"],
    },
    {
      title: "Apply the filter at the repository",
      detail: "Compose the EF Core query with a conditional Where clause so translation happens in PostgreSQL.",
      files: ["IOrderRepository.cs", "OrderRepository.cs"],
    },
    {
      title: "Validate and surface the parameter",
      detail: "Bind `status` in OrdersController.GetOrders and add FluentValidation for allowed enum values.",
      files: ["OrderController.cs"],
    },
    {
      title: "Wire the UI filter",
      detail: "Add a status dropdown to OrdersPage and pass the parameter through the API client.",
      files: ["OrdersPage.tsx"],
    },
    {
      title: "Cover with tests",
      detail: "Add unit tests for the filter and an integration test for the endpoint contract.",
      files: ["OrderServiceTests.cs"],
    },
  ],
}

export interface AffectedFile {
  name: string
  path: string
  project: string
  reason: string
  changeType: "modified" | "added"
  additions: number
  deletions: number
  confidence: number
}

export const affectedFiles: AffectedFile[] = [
  {
    name: "OrderController.cs",
    path: "src/DevPilot.Api/Controllers/OrdersController.cs",
    project: "DevPilot.Api",
    reason: "Binds the new `status` query parameter and forwards it to the query handler.",
    changeType: "modified",
    additions: 14,
    deletions: 3,
    confidence: 95,
  },
  {
    name: "OrderService.cs",
    path: "src/DevPilot.Application/Orders/OrderService.cs",
    project: "DevPilot.Application",
    reason: "Passes the status filter into the repository query and shapes the response.",
    changeType: "modified",
    additions: 9,
    deletions: 2,
    confidence: 93,
  },
  {
    name: "IOrderRepository.cs",
    path: "src/DevPilot.Domain/Orders/IOrderRepository.cs",
    project: "DevPilot.Domain",
    reason: "Contract change: adds an optional status argument to GetOrdersAsync.",
    changeType: "modified",
    additions: 3,
    deletions: 1,
    confidence: 90,
  },
  {
    name: "OrderRepository.cs",
    path: "src/DevPilot.Infrastructure/Persistence/OrderRepository.cs",
    project: "DevPilot.Infrastructure",
    reason: "Adds a translatable Where clause so filtering runs in PostgreSQL.",
    changeType: "modified",
    additions: 11,
    deletions: 2,
    confidence: 88,
  },
  {
    name: "OrdersPage.tsx",
    path: "src/DevPilot.Web/pages/OrdersPage.tsx",
    project: "DevPilot.Web",
    reason: "Adds a status dropdown and threads the selection through the API client.",
    changeType: "modified",
    additions: 22,
    deletions: 4,
    confidence: 86,
  },
  {
    name: "OrderServiceTests.cs",
    path: "src/DevPilot.Tests/Orders/OrderServiceTests.cs",
    project: "DevPilot.Tests",
    reason: "New tests covering the status filter behavior and validation.",
    changeType: "added",
    additions: 48,
    deletions: 0,
    confidence: 91,
  },
]

export const impactSummary = {
  apiChanges: [
    { label: "GET /api/orders", detail: "New optional query param `status` (enum). Backward compatible.", tone: "amber" as Tone },
  ],
  database: [
    { label: "No schema change", detail: "Uses existing Orders.Status column and index IX_Orders_Status.", tone: "green" as Tone },
  ],
  integrations: [
    { label: "No external impact", detail: "No third-party contracts or webhooks touched.", tone: "green" as Tone },
  ],
  tests: [
    { label: "5 tests required", detail: "3 unit · 2 integration. Estimated coverage +1.4%.", tone: "blue" as Tone },
  ],
}

/* ------------------------------- Execution --------------------------------- */

export type StageKey = "analyze" | "plan" | "approved" | "implement" | "build" | "review" | "pr"

export const stages: { key: StageKey; label: string }[] = [
  { key: "analyze", label: "Analyze" },
  { key: "plan", label: "Plan" },
  { key: "approved", label: "Approved" },
  { key: "implement", label: "Implement" },
  { key: "build", label: "Build & Test" },
  { key: "review", label: "Review" },
  { key: "pr", label: "Pull Request" },
]

export type ActivityKind = "read" | "edit" | "run" | "build" | "test" | "note" | "success" | "error"

export interface ActivityEntry {
  id: string
  time: string
  kind: ActivityKind
  agent: "Planner" | "Developer" | "Reviewer" | "System"
  message: string
  detail?: string
}

export const executionActivity: ActivityEntry[] = [
  { id: "a1", time: "00:00", kind: "note", agent: "System", message: "Execution approved by You", detail: "TASK-142 · feat/orders-status-filter" },
  { id: "a2", time: "00:02", kind: "read", agent: "Developer", message: "Reading OrdersController.cs", detail: "src/DevPilot.Api/Controllers" },
  { id: "a3", time: "00:04", kind: "read", agent: "Developer", message: "Reading GetOrdersQuery.cs", detail: "resolving query pipeline" },
  { id: "a4", time: "00:07", kind: "edit", agent: "Developer", message: "Editing OrderFilter.cs", detail: "+6 −1 · added Status property" },
  { id: "a5", time: "00:11", kind: "edit", agent: "Developer", message: "Editing OrderRepository.cs", detail: "+11 −2 · conditional Where clause" },
  { id: "a6", time: "00:15", kind: "edit", agent: "Developer", message: "Editing OrdersController.cs", detail: "+14 −3 · bind status param" },
  { id: "a7", time: "00:19", kind: "run", agent: "Developer", message: "dotnet format", detail: "0 issues" },
  { id: "a8", time: "00:24", kind: "build", agent: "System", message: "dotnet build DevPilot.sln", detail: "Build succeeded · 0 warnings" },
  { id: "a9", time: "00:31", kind: "test", agent: "System", message: "dotnet test", detail: "42 passed · 0 failed · 3.1s" },
  { id: "a10", time: "00:36", kind: "note", agent: "Reviewer", message: "Reviewing diff for correctness & risk", detail: "6 files" },
  { id: "a11", time: "00:41", kind: "success", agent: "Reviewer", message: "Filter translated to SQL — no in-memory evaluation", detail: "verified IQueryable composition" },
]

export const execution = {
  taskId: "TASK-142",
  currentStage: "review" as StageKey,
  elapsed: "00:41",
  tokensUsed: 184_920,
  tokenBudget: 400_000,
  estCost: 1.42,
  model: "devpilot-coder-v2",
  agents: [
    { role: "Planner", status: "done" as const, note: "Produced 5-step plan" },
    { role: "Developer", status: "done" as const, note: "6 files changed" },
    { role: "Reviewer", status: "active" as const, note: "2 checks remaining" },
  ],
}

/* ---------------------- Per-run execution detail (by id) ------------------- */

export type RunStatus = "running" | "review-ready" | "blocked" | "failed" | "merged"

export const runStatusMeta: Record<RunStatus, { label: string; tone: Tone }> = {
  running: { label: "Running", tone: "blue" },
  "review-ready": { label: "Review ready", tone: "green" },
  blocked: { label: "Blocked", tone: "amber" },
  failed: { label: "Failed", tone: "red" },
  merged: { label: "Merged", tone: "green" },
}

export interface RunAgent {
  role: "Planner" | "Developer" | "Reviewer"
  status: "done" | "active" | "idle" | "failed" | "blocked"
  note: string
}

export interface RunAlert {
  kind: "failed" | "blocked"
  title: string
  detail: string
  at: string
  logExcerpt?: string
  remediations: { label: string; primary?: boolean }[]
}

export interface RunDetail {
  id: string
  taskId: string
  title: string
  branch: string
  status: RunStatus
  currentStage: StageKey
  stream: boolean
  elapsed: string
  tokensUsed: number
  tokenBudget: number
  estCost: number
  model: string
  agents: RunAgent[]
  activity: ActivityEntry[]
  build: { status: "passed" | "failed" | "pending"; detail: string }
  tests: { status: "passed" | "failed" | "pending"; passed: number; failed: number; detail: string }
  alert?: RunAlert
}

export const runDetails: Record<string, RunDetail> = {
  "EXEC-142": {
    id: "EXEC-142",
    taskId: "TASK-142",
    title: "Add status filtering to the orders endpoint",
    branch: "feat/orders-status-filter",
    status: "running",
    currentStage: "review",
    stream: true,
    elapsed: "00:41",
    tokensUsed: 184_920,
    tokenBudget: 400_000,
    estCost: 1.42,
    model: "devpilot-coder-v2",
    agents: [
      { role: "Planner", status: "done", note: "Produced 5-step plan" },
      { role: "Developer", status: "done", note: "6 files changed" },
      { role: "Reviewer", status: "active", note: "2 checks remaining" },
    ],
    activity: executionActivity,
    build: { status: "passed", detail: "dotnet build · 0 warnings · 8.2s" },
    tests: { status: "passed", passed: 42, failed: 0, detail: "dotnet test · 3.1s" },
  },
  "EXEC-141": {
    id: "EXEC-141",
    taskId: "TASK-141",
    title: "Add pagination metadata to product listing",
    branch: "feat/product-pagination",
    status: "running",
    currentStage: "implement",
    stream: true,
    elapsed: "00:18",
    tokensUsed: 92_400,
    tokenBudget: 400_000,
    estCost: 0.71,
    model: "devpilot-coder-v2",
    agents: [
      { role: "Planner", status: "done", note: "Produced 4-step plan" },
      { role: "Developer", status: "active", note: "editing ProductsController.cs" },
      { role: "Reviewer", status: "idle", note: "waiting for build" },
    ],
    activity: [
      { id: "p1", time: "00:00", kind: "note", agent: "System", message: "Execution approved by You", detail: "TASK-141 · feat/product-pagination" },
      { id: "p2", time: "00:03", kind: "read", agent: "Developer", message: "Reading ProductsController.cs", detail: "src/DevPilot.Api/Controllers" },
      { id: "p3", time: "00:06", kind: "read", agent: "Developer", message: "Reading GetProductsQuery.cs", detail: "resolving query pipeline" },
      { id: "p4", time: "00:10", kind: "edit", agent: "Developer", message: "Editing PagedResult.cs", detail: "+18 −0 · new envelope type" },
      { id: "p5", time: "00:15", kind: "edit", agent: "Developer", message: "Editing ProductsController.cs", detail: "+9 −2 · return page metadata" },
    ],
    build: { status: "pending", detail: "not started" },
    tests: { status: "pending", passed: 0, failed: 0, detail: "not started" },
  },
  "EXEC-140": {
    id: "EXEC-140",
    taskId: "TASK-140",
    title: "Cache product catalog responses",
    branch: "perf/product-cache",
    status: "blocked",
    currentStage: "review",
    stream: false,
    elapsed: "02:05",
    tokensUsed: 268_300,
    tokenBudget: 400_000,
    estCost: 2.08,
    model: "devpilot-coder-v2",
    agents: [
      { role: "Planner", status: "done", note: "Produced 6-step plan" },
      { role: "Developer", status: "done", note: "6 files changed" },
      { role: "Reviewer", status: "blocked", note: "needs human decision" },
    ],
    activity: [
      { id: "c1", time: "00:00", kind: "note", agent: "System", message: "Execution approved by You", detail: "TASK-140 · perf/product-cache" },
      { id: "c2", time: "00:05", kind: "edit", agent: "Developer", message: "Editing ProductService.cs", detail: "+34 −6 · wrap reads in cache" },
      { id: "c3", time: "00:12", kind: "edit", agent: "Developer", message: "Adding RedisCacheProvider.cs", detail: "+88 −0 · distributed cache" },
      { id: "c4", time: "00:20", kind: "build", agent: "System", message: "dotnet build DevPilot.sln", detail: "Build succeeded · 0 warnings" },
      { id: "c5", time: "00:28", kind: "test", agent: "System", message: "dotnet test", detail: "51 passed · 0 failed · 4.0s" },
      { id: "c6", time: "01:40", kind: "note", agent: "Reviewer", message: "Reviewing cache invalidation strategy", detail: "ProductService.cs" },
      { id: "c7", time: "02:05", kind: "error", agent: "Reviewer", message: "Blocked: no invalidation on product update", detail: "stale reads possible after PUT /api/products/{id}" },
    ],
    build: { status: "passed", detail: "dotnet build · 0 warnings · 9.1s" },
    tests: { status: "passed", passed: 51, failed: 0, detail: "dotnet test · 4.0s" },
    alert: {
      kind: "blocked",
      title: "Reviewer paused the run for a human decision",
      detail:
        "The cache is populated on read but never invalidated when a product is updated via PUT /api/products/{id}. This can serve stale catalog data. DevPilot won't guess a TTL or eviction policy on a correctness-sensitive path — it needs your call.",
      at: "Review · check 3 of 4",
      logExcerpt:
        "reviewer: cache write path OK\nreviewer: cache read path OK\nreviewer: FAIL invalidation — no eviction hook on ProductUpdated\nreviewer: escalating to human (risk=medium)",
      remediations: [
        { label: "Add invalidation & continue", primary: true },
        { label: "Set a 60s TTL instead" },
        { label: "Edit plan" },
        { label: "Cancel run" },
      ],
    },
  },
  "EXEC-139": {
    id: "EXEC-139",
    taskId: "TASK-139",
    title: "Enforce role claims on customer endpoints",
    branch: "sec/customer-role-claims",
    status: "merged",
    currentStage: "pr",
    stream: false,
    elapsed: "01:12",
    tokensUsed: 121_000,
    tokenBudget: 400_000,
    estCost: 0.93,
    model: "devpilot-coder-v2",
    agents: [
      { role: "Planner", status: "done", note: "Produced 3-step plan" },
      { role: "Developer", status: "done", note: "3 files changed" },
      { role: "Reviewer", status: "done", note: "approved · merged" },
    ],
    activity: [
      { id: "r1", time: "00:00", kind: "note", agent: "System", message: "Execution approved by You", detail: "TASK-139 · sec/customer-role-claims" },
      { id: "r2", time: "00:08", kind: "edit", agent: "Developer", message: "Editing CustomersController.cs", detail: "+7 −1 · [Authorize(Roles=...)]" },
      { id: "r3", time: "00:22", kind: "build", agent: "System", message: "dotnet build DevPilot.sln", detail: "Build succeeded" },
      { id: "r4", time: "00:40", kind: "test", agent: "System", message: "dotnet test", detail: "39 passed · 0 failed" },
      { id: "r5", time: "01:00", kind: "success", agent: "Reviewer", message: "Approved — role claims enforced", detail: "no regressions" },
      { id: "r6", time: "01:12", kind: "success", agent: "System", message: "Pull request merged to master", detail: "#482 · squash merge" },
    ],
    build: { status: "passed", detail: "dotnet build · 0 warnings · 7.4s" },
    tests: { status: "passed", passed: 39, failed: 0, detail: "dotnet test · 2.8s" },
  },
  "EXEC-138": {
    id: "EXEC-138",
    taskId: "TASK-138",
    title: "Migrate customer notifications to outbox pattern",
    branch: "refactor/notification-outbox",
    status: "failed",
    currentStage: "build",
    stream: false,
    elapsed: "00:54",
    tokensUsed: 156_800,
    tokenBudget: 400_000,
    estCost: 1.21,
    model: "devpilot-coder-v2",
    agents: [
      { role: "Planner", status: "done", note: "Produced 7-step plan" },
      { role: "Developer", status: "done", note: "9 files changed" },
      { role: "Reviewer", status: "idle", note: "never reached" },
    ],
    activity: [
      { id: "o1", time: "00:00", kind: "note", agent: "System", message: "Execution approved by A. Demir", detail: "TASK-138 · refactor/notification-outbox" },
      { id: "o2", time: "00:04", kind: "read", agent: "Developer", message: "Reading NotificationService.cs", detail: "src/DevPilot.Infrastructure/Notifications" },
      { id: "o3", time: "00:09", kind: "edit", agent: "Developer", message: "Adding OutboxMessage.cs", detail: "+42 −0 · new entity" },
      { id: "o4", time: "00:16", kind: "edit", agent: "Developer", message: "Editing AppDbContext.cs", detail: "+11 −0 · DbSet<OutboxMessage>" },
      { id: "o5", time: "00:23", kind: "edit", agent: "Developer", message: "Editing NotificationService.cs", detail: "+37 −14 · write to outbox" },
      { id: "o6", time: "00:38", kind: "build", agent: "System", message: "dotnet build DevPilot.sln", detail: "Build succeeded · 0 warnings" },
      { id: "o7", time: "00:54", kind: "error", agent: "System", message: "dotnet test failed — pending model changes", detail: "42 passed · 3 failed" },
    ],
    build: { status: "passed", detail: "dotnet build · 0 warnings · 8.6s" },
    tests: { status: "failed", passed: 42, failed: 3, detail: "dotnet test · 3.4s · 3 failing" },
    alert: {
      kind: "failed",
      title: "Execution failed during Build & Test",
      detail:
        "The EF Core model changed (a new OutboxMessage entity was added to AppDbContext) but no migration was generated. Three integration tests fail because the schema and the model are out of sync.",
      at: "Build & Test · step 5 of 7",
      logExcerpt:
        "System.InvalidOperationException: The model for context 'AppDbContext' has pending changes.\n  Add a new migration before updating the database.\n  → dotnet ef migrations add AddOutboxMessage\n  Failing: OutboxDispatchTests (3)",
      remediations: [
        { label: "Generate migration & retry", primary: true },
        { label: "Re-run failed tests" },
        { label: "View full logs" },
        { label: "Abandon run" },
      ],
    },
  },
}

/* ------------------------------ Code review -------------------------------- */

export interface DiffLine {
  type: "context" | "add" | "del" | "hunk"
  content: string
  oldNo?: number
  newNo?: number
}

export const diffFiles: {
  name: string
  path: string
  additions: number
  deletions: number
  note: string
  lines: DiffLine[]
}[] = [
  {
    name: "OrderController.cs",
    path: "src/DevPilot.Api/Controllers/OrdersController.cs",
    additions: 14,
    deletions: 3,
    note: "Binds the optional status parameter and validates the enum before dispatching the query.",
    lines: [
      { type: "hunk", content: "@@ -22,9 +22,16 @@ public class OrdersController : ControllerBase" },
      { type: "context", content: "    [HttpGet]", oldNo: 22, newNo: 22 },
      { type: "del", content: "    public async Task<IActionResult> GetOrders()", oldNo: 23 },
      { type: "add", content: "    public async Task<IActionResult> GetOrders([FromQuery] string? status)", newNo: 23 },
      { type: "context", content: "    {", oldNo: 24, newNo: 24 },
      { type: "add", content: "        if (status is not null && !Enum.TryParse<OrderStatus>(status, true, out _))", newNo: 25 },
      { type: "add", content: "            return BadRequest($\"Unknown order status '{status}'.\");", newNo: 26 },
      { type: "add", content: "", newNo: 27 },
      { type: "del", content: "        var result = await _orderService.GetOrdersAsync();", oldNo: 25 },
      { type: "add", content: "        var result = await _orderService.GetOrdersAsync(status);", newNo: 28 },
      { type: "context", content: "        return Ok(result);", oldNo: 26, newNo: 29 },
      { type: "context", content: "    }", oldNo: 27, newNo: 30 },
    ],
  },
  {
    name: "OrderRepository.cs",
    path: "src/DevPilot.Infrastructure/Persistence/OrderRepository.cs",
    additions: 11,
    deletions: 2,
    note: "Applies the filter through IQueryable so PostgreSQL performs the WHERE — confirmed no client-side evaluation.",
    lines: [
      { type: "hunk", content: "@@ -18,7 +18,14 @@ public class OrderRepository : IOrderRepository" },
      { type: "del", content: "    public async Task<IReadOnlyList<Order>> GetOrdersAsync()", oldNo: 18 },
      { type: "add", content: "    public async Task<IReadOnlyList<Order>> GetOrdersAsync(OrderStatus? status = null)", newNo: 18 },
      { type: "context", content: "    {", oldNo: 19, newNo: 19 },
      { type: "add", content: "        var query = _db.Orders.AsQueryable();", newNo: 20 },
      { type: "add", content: "        if (status is not null)", newNo: 21 },
      { type: "add", content: "            query = query.Where(o => o.Status == status);", newNo: 22 },
      { type: "add", content: "", newNo: 23 },
      { type: "del", content: "        return await _db.Orders.ToListAsync();", oldNo: 20 },
      { type: "add", content: "        return await query.ToListAsync();", newNo: 24 },
      { type: "context", content: "    }", oldNo: 21, newNo: 25 },
    ],
  },
  {
    name: "OrdersPage.tsx",
    path: "src/DevPilot.Web/pages/OrdersPage.tsx",
    additions: 22,
    deletions: 4,
    note: "Adds a controlled status dropdown and passes the value to the orders API client.",
    lines: [
      { type: "hunk", content: "@@ -8,6 +8,11 @@ export function OrdersPage() {" },
      { type: "add", content: "  const [status, setStatus] = useState<string>('all')", newNo: 8 },
      { type: "context", content: "  const { data } = useOrders({ status })", oldNo: 8, newNo: 9 },
      { type: "add", content: "  const options = ['all', 'open', 'shipped', 'cancelled']", newNo: 10 },
      { type: "context", content: "  return (", oldNo: 9, newNo: 11 },
      { type: "context", content: "    <div className=\"orders\">", oldNo: 10, newNo: 12 },
      { type: "add", content: "      <StatusFilter value={status} options={options} onChange={setStatus} />", newNo: 13 },
      { type: "context", content: "      <OrderTable rows={data} />", oldNo: 11, newNo: 14 },
    ],
  },
]

export const reviewSummary = {
  build: { status: "passed" as const, detail: "dotnet build · 0 warnings · 8.2s" },
  tests: { status: "passed" as const, passed: 42, failed: 0, added: 5, detail: "dotnet test · 3.1s" },
  coverage: { before: 78.2, after: 79.6 },
  reviewerNotes: [
    { tone: "green" as Tone, title: "SQL translation verified", body: "The status filter composes on IQueryable and translates to a single WHERE — no in-memory filtering." },
    { tone: "amber" as Tone, title: "Consider case-insensitive parsing note", body: "Enum.TryParse uses ignoreCase; document accepted values in the OpenAPI spec." },
    { tone: "neutral" as Tone, title: "Backward compatibility holds", body: "Calls without `status` hit the same code path as before." },
  ],
  risks: [
    { tone: "amber" as Tone, text: "New query param is undocumented in Swagger until the XML doc comment is added." },
    { tone: "green" as Tone, text: "No database migration required." },
  ],
}

/* ------------------------------ Project Brain ------------------------------ */

export interface BrainCitation {
  file: string
  path: string
  lines: string
  snippet: string
  startLine?: number
  symbol?: string
  lang?: "cs" | "ts" | "tsx"
}

export interface BrainMessage {
  role: "user" | "assistant"
  content: string
  citations?: BrainCitation[]
  confidence?: number
  elapsed?: string
}

export const brainSuggested = [
  "How does authentication work?",
  "Where is the order pricing calculated?",
  "What validates incoming API requests?",
  "How is the database configured?",
  "Which endpoints require authorization?",
]

export const brainConversation: BrainMessage[] = [
  {
    role: "user",
    content: "How does authentication work?",
  },
  {
    role: "assistant",
    confidence: 94,
    elapsed: "1.8s",
    content:
      "DevPilot uses stateless JWT authentication. On POST /api/auth/login, `AuthService` validates the credentials and calls `JwtTokenService` to issue a signed access token plus a refresh token. Tokens are validated by the JWT bearer middleware registered in `Program.cs`, which wires up the signing key, issuer and audience. Protected controllers rely on the `[Authorize]` attribute, and role checks use claims embedded in the token.",
    citations: [
      {
        file: "AuthService.cs",
        path: "src/DevPilot.Infrastructure/Auth/AuthService.cs",
        lines: "L34–L58",
        startLine: 34,
        symbol: "AuthService.AuthenticateAsync",
        lang: "cs",
        snippet:
          "public async Task<AuthResult> AuthenticateAsync(string email, string password)\n{\n    var user = await _users.FindByEmailAsync(email);\n    if (user is null) return AuthResult.Fail(\"Invalid credentials\");\n\n    if (!_hasher.Verify(password, user.PasswordHash))\n        return AuthResult.Fail(\"Invalid credentials\");\n\n    return AuthResult.Success(_tokens.Issue(user));\n}",
      },
      {
        file: "JwtTokenService.cs",
        path: "src/DevPilot.Infrastructure/Auth/JwtTokenService.cs",
        lines: "L21–L47",
        startLine: 21,
        symbol: "JwtTokenService.Issue",
        lang: "cs",
        snippet:
          "public TokenPair Issue(User user)\n{\n    var claims = BuildClaims(user);\n    var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);\n    var token = new JwtSecurityToken(\n        issuer: _issuer,\n        audience: _audience,\n        claims: claims,\n        expires: _clock.UtcNow.AddMinutes(30),\n        signingCredentials: creds);\n    return new TokenPair(Write(token), IssueRefresh(user));\n}",
      },
      {
        file: "Program.cs",
        path: "src/DevPilot.Api/Program.cs",
        lines: "L40–L63",
        startLine: 40,
        symbol: "Program (composition root)",
        lang: "cs",
        snippet:
          "builder.Services\n    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)\n    .AddJwtBearer(o =>\n    {\n        o.TokenValidationParameters = new TokenValidationParameters\n        {\n            ValidIssuer = cfg[\"Jwt:Issuer\"],\n            ValidAudience = cfg[\"Jwt:Audience\"],\n            IssuerSigningKey = signingKey,\n            ValidateIssuerSigningKey = true,\n        };\n    });",
      },
    ],
  },
  {
    role: "user",
    content: "Where is the order pricing calculated?",
  },
  {
    role: "assistant",
    confidence: 89,
    elapsed: "2.1s",
    content:
      "Order pricing was recently extracted into a dedicated domain service. `OrderPricingService.Price` computes the line totals, applies any active discount policy and returns an immutable `PricedOrder`. It is invoked from `OrderService.CreateOrder` in the application layer — the controller never touches pricing directly, so the rule stays in the domain.",
    citations: [
      {
        file: "OrderPricingService.cs",
        path: "src/DevPilot.Domain/Orders/OrderPricingService.cs",
        lines: "L12–L34",
        startLine: 12,
        symbol: "OrderPricingService.Price",
        lang: "cs",
        snippet:
          "public PricedOrder Price(Order order, DiscountPolicy policy)\n{\n    var lines = order.Items\n        .Select(i => new PricedLine(i.Sku, i.Qty, i.UnitPrice, i.Qty * i.UnitPrice))\n        .ToList();\n\n    var subtotal = lines.Sum(l => l.Total);\n    var discount = policy.AmountFor(subtotal);\n    return new PricedOrder(lines, subtotal, discount, subtotal - discount);\n}",
      },
      {
        file: "OrderService.cs",
        path: "src/DevPilot.Application/Orders/OrderService.cs",
        lines: "L48–L61",
        startLine: 48,
        symbol: "OrderService.CreateOrder",
        lang: "cs",
        snippet:
          "var policy = await _discounts.ActivePolicyAsync(customerId, ct);\nvar priced = _pricing.Price(order, policy);\norder.ApplyPricing(priced);\nawait _repo.AddAsync(order, ct);",
      },
    ],
  },
]

export const brainContextFiles = [
  { file: "AuthService.cs", path: "src/DevPilot.Infrastructure/Auth", relevance: 96 },
  { file: "JwtTokenService.cs", path: "src/DevPilot.Infrastructure/Auth", relevance: 91 },
  { file: "Program.cs", path: "src/DevPilot.Api", relevance: 84 },
  { file: "AuthController.cs", path: "src/DevPilot.Api/Controllers", relevance: 79 },
]

/* Indexed source groups shown in the Brain knowledge rail */
export const brainSourceGroups = [
  { project: "DevPilot.Api", layer: "Web", files: 63, symbols: 812, indexed: true, tone: "blue" as Tone },
  { project: "DevPilot.Application", layer: "Application", files: 148, symbols: 2411, indexed: true, tone: "amber" as Tone },
  { project: "DevPilot.Domain", layer: "Domain", files: 92, symbols: 1980, indexed: true, tone: "green" as Tone },
  { project: "DevPilot.Infrastructure", layer: "Infrastructure", files: 111, symbols: 2106, indexed: true, tone: "neutral" as Tone },
  { project: "DevPilot.Web", layer: "Web", files: 174, symbols: 964, indexed: true, tone: "blue" as Tone },
  { project: "DevPilot.Tests", layer: "Tests", files: 24, symbols: 148, indexed: true, tone: "gray" as Tone },
]

/* ------------------------------ Architecture ------------------------------- */

export interface ArchNode {
  id: string
  label: string
  sub: string
  x: number
  y: number
  layer: string
  tone: Tone
  impacted?: boolean
  incoming: string[]
  outgoing: string[]
  files: string[]
  why: string
}

export const archNodes: ArchNode[] = [
  {
    id: "web",
    label: "DevPilot.Web",
    sub: "React SPA",
    x: 60,
    y: 60,
    layer: "Presentation",
    tone: "blue",
    impacted: true,
    incoming: [],
    outgoing: ["api"],
    files: ["OrdersPage.tsx", "ordersApi.ts"],
    why: "The orders page gains a status dropdown and passes the selected filter to the API client.",
  },
  {
    id: "api",
    label: "DevPilot.Api",
    sub: "ASP.NET Core",
    x: 60,
    y: 210,
    layer: "Web",
    tone: "blue",
    impacted: true,
    incoming: ["web"],
    outgoing: ["application"],
    files: ["OrdersController.cs", "Program.cs"],
    why: "OrdersController binds and validates the new status query parameter before dispatch.",
  },
  {
    id: "application",
    label: "DevPilot.Application",
    sub: "Use cases · MediatR",
    x: 60,
    y: 360,
    layer: "Application",
    tone: "amber",
    impacted: true,
    incoming: ["api"],
    outgoing: ["domain", "infrastructure"],
    files: ["OrderService.cs", "GetOrdersQuery.cs", "OrderFilter.cs"],
    why: "The query and filter contracts carry the optional status through the pipeline.",
  },
  {
    id: "domain",
    label: "DevPilot.Domain",
    sub: "Entities · Contracts",
    x: 330,
    y: 360,
    layer: "Domain",
    tone: "green",
    impacted: true,
    incoming: ["application", "infrastructure"],
    outgoing: [],
    files: ["Order.cs", "OrderStatus.cs", "IOrderRepository.cs"],
    why: "IOrderRepository gains an optional status argument; OrderStatus enum drives validation.",
  },
  {
    id: "infrastructure",
    label: "DevPilot.Infrastructure",
    sub: "EF Core · Persistence",
    x: 60,
    y: 510,
    layer: "Infrastructure",
    tone: "neutral",
    impacted: true,
    incoming: ["application"],
    outgoing: ["postgres", "domain"],
    files: ["OrderRepository.cs", "AppDbContext.cs"],
    why: "OrderRepository composes a translatable WHERE clause so filtering executes in the database.",
  },
  {
    id: "postgres",
    label: "PostgreSQL",
    sub: "Orders · Products",
    x: 330,
    y: 510,
    layer: "Data",
    tone: "gray",
    impacted: false,
    incoming: ["infrastructure"],
    outgoing: [],
    files: ["IX_Orders_Status"],
    why: "No schema change — the existing Status column and its index serve the filter.",
  },
]

export const archEdges: { from: string; to: string }[] = [
  { from: "web", to: "api" },
  { from: "api", to: "application" },
  { from: "application", to: "domain" },
  { from: "application", to: "infrastructure" },
  { from: "infrastructure", to: "domain" },
  { from: "infrastructure", to: "postgres" },
]

/* ------------------------------ Recent activity ---------------------------- */

export interface ActivityFeedItem {
  id: string
  actor: "Planner" | "Developer" | "Reviewer" | "You" | "System"
  action: string
  target: string
  time: string
  tone: Tone
}

export const recentActivity: ActivityFeedItem[] = [
  { id: "f1", actor: "Reviewer", action: "verified SQL translation on", target: "TASK-142", time: "2m", tone: "green" },
  { id: "f2", actor: "Developer", action: "changed 6 files in", target: "feat/orders-status-filter", time: "5m", tone: "blue" },
  { id: "f3", actor: "System", action: "build failed on", target: "TASK-138", time: "1h", tone: "red" },
  { id: "f4", actor: "You", action: "merged", target: "TASK-139", time: "2h", tone: "green" },
  { id: "f5", actor: "Planner", action: "produced a plan for", target: "TASK-141", time: "3h", tone: "neutral" },
  { id: "f6", actor: "System", action: "re-indexed", target: "enesscigdem/DevPilot", time: "6m", tone: "neutral" },
]

export const commandItems = [
  { label: "Go to Workspace", hint: "Home", href: "/", group: "Navigate" },
  { label: "Open Project Workspace", hint: "enesscigdem/DevPilot", href: "/projects", group: "Navigate" },
  { label: "View Tasks", hint: "6 active", href: "/tasks", group: "Navigate" },
  { label: "Open Project Brain", hint: "Ask the codebase", href: "/brain", group: "Navigate" },
  { label: "View Executions", hint: "1 running", href: "/executions", group: "Navigate" },
  { label: "Architecture Map", hint: "Impact graph", href: "/architecture", group: "Navigate" },
  { label: "Review plan: Add status filtering", hint: "TASK-142", href: "/tasks/TASK-142", group: "Tasks" },
  { label: "Inspect failure: outbox migration", hint: "TASK-138", href: "/executions/EXEC-138", group: "Tasks" },
  { label: "Create new task", hint: "Describe an engineering change", href: "/tasks", group: "Actions" },
  { label: "Re-analyze codebase", hint: "Roslyn workspace", href: "/projects", group: "Actions" },
]
