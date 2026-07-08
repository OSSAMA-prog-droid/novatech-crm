# NovaTech CRM — Order Processing Service

Internal CRM and order management system for NovaTech Solutions.

<!-- NOVA-123: test push via extension - 2026-07-08 -->

## Architecture

```
src/
  NovaTechCRM.Api/           ASP.NET Core Web API
  NovaTechCRM.Services/      Business logic layer
  NovaTechCRM.Repositories/  Data access layer (EF Core)
  NovaTechCRM.Domain/        Domain models
tests/
  NovaTechCRM.Tests/         Unit tests (xUnit)
```

## Getting Started

```bash
dotnet restore
dotnet build
dotnet run --project src/NovaTechCRM.Api
```

## Known Issues

See the internal ticket tracker for open issues. Current sprint tickets are tagged `NOVA-*`.

## Stack

- .NET 8
- ASP.NET Core 8 (Web API)
- Entity Framework Core 8 (SQL Server)
- xUnit + Moq (tests)
- FraudShield SDK (fraud detection — external service)
