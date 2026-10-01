# DevPilot

AI-powered Software Development & Delivery Platform.

## Tech Stack

- **Backend**: .NET 10 LTS ASP.NET Core Web API
- **Frontend**: React 19 + TypeScript + Vite
- **Database**: PostgreSQL + pgvector (upcoming)
- **AI Provider**: Provider-independent abstraction, first provider Kimi K3 (upcoming)
- **Git**: Provider abstraction, first adapter GitHub (upcoming)
- **Containers**: Docker / Docker Compose

## Project Structure

```
DevPilot/
├── src/
│   ├── DevPilot.Api/           # ASP.NET Core Web API
│   ├── DevPilot.Application/   # Use cases and business logic
│   ├── DevPilot.Domain/        # Domain entities and interfaces
│   ├── DevPilot.Infrastructure/# External services, data, providers
│   └── DevPilot.Web/           # React 19 + TypeScript + Vite frontend
├── Dockerfile.Api
├── Dockerfile.Web
├── docker-compose.yml
└── DevPilot.sln
```

## Getting Started

### Prerequisites

- .NET 10 SDK
- Node.js 20+
- Docker + Docker Compose

### Run Backend

```bash
cd src/DevPilot.Api
dotnet run
```

### Run Frontend

```bash
cd src/DevPilot.Web
npm install
npm run dev
```

### Run with Docker Compose

```bash
docker compose up --build
```

## Architecture

The solution follows Clean Architecture / Modular Monolith principles:

- `DevPilot.Domain` has no external project references.
- `DevPilot.Application` references `DevPilot.Domain`.
- `DevPilot.Infrastructure` references `DevPilot.Application` and `DevPilot.Domain`.
- `DevPilot.Api` references `DevPilot.Application` and `DevPilot.Infrastructure`.

## Configuration notes

- `RepositoryClone:WorkspaceRoot` is intentionally empty in the committed `appsettings.json`. When empty, DevPilot
  uses a per-user app-data directory (`<ApplicationData>/DevPilot/Workspaces`). Override with
  `RepositoryClone__WorkspaceRoot=/path/to/workspaces` (environment variable) or user secrets.
- Execution and generation limits (repair rounds, flake confirmation, generation calls, concurrency, token budgets)
  live in the single `ExecutionReliability` section. Legacy `DeveloperAgent:*` keys are still honored as fallbacks.
- `Hangfire:WorkerCount` (default 2) caps parallel executions; each one runs builds, tests and AI calls.
- `AiPricing:InputPerMillionTokensUsd` / `OutputPerMillionTokensUsd` are optional. Without both, token counts are shown but no cost estimate is.
