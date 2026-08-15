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
