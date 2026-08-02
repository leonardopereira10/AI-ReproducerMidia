---
name: complexity-eval
description: |
  Avaliar complexidade de subtask para rotear ao agente correto. Carregar quando
  uma task não tem campo 'complexidade' definido, antes de delegar para
  developer-{nivel} ou qa-tester-{nivel}. Retorna: baixa|media|alta|critical.
  Keywords: complexidade, avaliar, rotear, classificar, nível, agente, delegar.
  Ator: sessão principal (orquestrador), product-owner.
---

# Complexity Evaluation

## Purpose

Evaluate task complexity and return a single level. Used by orchestrator to route tasks to correct agent.

## Complexity Levels

| Level | Dev Scope | QA Scope |
|-------|-----------|----------|
| `baixa` | CRUD simples, testes unitários básicos, validações, DTO mapping | CRUD básico, validação de formulário simples |
| `media` | Serviços genéricos, converters, validators, paginação, dynamic search | Mocks complexos, validação de serviços genéricos, pagination |
| `alta` | Repositórios customizados, fluxos complexos, auth, E2E tests | E2E tests, multi-layer flows, auth integration, performance |
| `critical` | Zero-defect, security-sensitive, critical production paths | Zero-defect, OWASP Top 10, chaos engineering, memory analysis |

## Usage

When orchestrator receives a task without `complexidade` field:

1. Invoke this skill with the task description
2. Skill returns ONLY: `{complexidade: 'baixa'|'media'|'alta'|'critical'}`
3. Orchestrator routes to matching agent

## Rules

- Prompt must contain ONLY the task description (no detailed steps)
- Return ONLY the complexity level — no explanation
- After receiving result, do NOT reference the evaluation content in re-delegation
- Re-delegate with clean context — only the isolated subtask description
