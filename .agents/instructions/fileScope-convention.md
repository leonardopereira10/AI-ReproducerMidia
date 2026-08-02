# fileScope Convention — Concurrency Control

## Purpose
Prevent silent merge conflicts when multiple subtasks modify the same files.

## Rule
Every subtask in the ACP schema MUST declare a `fileScope` field listing all files it creates or modifies.

## Convention

### Format
```json
{
  "fileScope": ["RazeGas.Domain/Services/ClienteService.cs"],
  "lockMode": "exclusive"
}
```

### Path Format
- Always use forward slashes `/`
- Always relative to project root
- No trailing slashes

### lockMode Values
- `exclusive` — other subtasks touching same file must wait (default)
- `read` — subtask only reads these files, no blocking needed

### Execution Order
- Subtasks with **overlapping** `fileScope` → **sequential** (one after another)
- Subtasks with **non-overlapping** `fileScope` → **parallel** (simultaneous)
- Subtasks with **empty** `fileScope` → considered read-only, no blocking

## Responsibilities

| Role | Responsibility |
|------|---------------|
| **Orquestrador** | Detect overlaps before delegation; enforce sequential/parallel |
| **Dev agents** | Declare `fileScope` after implementation |
| **QA agents** | Declare `fileScope` after testing |
| **PO agent** | Include `fileScope` when generating subtasks |
| **engineer** | Audit `fileScope` compliance in agent configs |

## Anti-pattern
❌ Two subtasks modifying `ClienteService.cs` delegated in parallel
✅ Orquestrador detects overlap → schedules sequentially
