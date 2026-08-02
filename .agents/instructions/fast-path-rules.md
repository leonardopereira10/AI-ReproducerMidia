# Fast-Path Rules — Direct Execution

## Purpose
Avoid unnecessary sprint overhead for trivial tasks.

## Criteria (ALL must be met)
1. Change in **≤2 files**
2. **No** change to public interface (API, contract, enum)
3. **No** architectural change or business flow alteration

## Action
- If **ALL** criteria met → execute directly, no sprint, no subtasks, no agent delegation
- If **ANY** criterion fails → proceed with normal sprint flow

## Examples

| Task | Fast-path? | Why |
|------|-----------|-----|
| Fix typo in `appsettings.json` | ✅ Yes | 1 file, no public impact |
| Rename property in DTO | ✅ Yes | 1 file, no contract change |
| Add new entity | ❌ No | 7+ files, new contract |
| Implement state machine | ❌ No | Complex business flow |
