# Convenções de Teste — <PROJECT> Backend

## Defect JSON Schema

```json
{
  "id": "defect_001",
  "title": "Short description",
  "severity": "critical|high|medium|low",
  "component": "<PROJECT>.Domain.Services",
  "project": "<PROJECT>.Domain",
  "description": "Detailed description",
  "stepsToReproduce": ["step 1", "step 2"],
  "expectedBehavior": "What should happen",
  "actualBehavior": "What goes wrong",
  "evidence": "stack trace, logs",
  "priority": "P0|P1|P2|P3",
  "status": "open|fixed|reopened|closed",
  "complexidade": "baixa|media|alta|critical",
  "createdAt": "2026-07-08T12:00:00Z"
}
```

## Severity Classification

| Severity | Description | Example |
|----------|-------------|---------|
| **Critical** | App crash, data corruption, build fails | EF Core migration breaks, null ref in BaseService |
| **High** | Main functionality broken | CRUD fails, validation rules broken |
| **Medium** | Secondary functionality issues | Inconsistent DTO mapping, pagination edge cases |
| **Low** | Detail, docs, style | Wrong comment, missing XML doc |

## Test Levels

| Level | Agent | Tools |
|-------|-------|-------|
| Unit (simple) | `qa-tester-baixo` | xUnit, direct assertions |
| Unit (mocked) + Integration | `qa-tester-medio` | Moq, WebApplicationFactory |
| E2E + Performance | `qa-tester-alto` | WebApplicationFactory, performance metrics |
| Security + Chaos | `qa-tester-critical` | OWASP checks, failure injection |

## Defect Subtask Flow

1. Generate JSON subtask at `.agents\sprint_atual\sprint_defect_<number>.json`
2. Deliver to developer via orchestrator
3. Re-test after fix → update status (`open` → `fixed` or `reopened`)

## Important Rules

1. Verify build before testing
2. Register results clearly (passed/failed/blocked)
3. Classify defect severity
4. Include `complexidade` field in subtasks
5. `BLOCKED` = testing not possible (build fails, deps unavailable)
6. `PASSED` = works as expected
7. `FAILED` = bug present
