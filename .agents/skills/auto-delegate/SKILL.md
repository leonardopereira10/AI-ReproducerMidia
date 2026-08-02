---
name: auto-delegate
description: |
  Classificador obrigatório de mensagens do usuário. Carregar SEMPRE ao receber
  qualquer pedido antes de orquestrar. Classifica intenção em EXECUTE_SPRINT,
  PLAN_SPRINT ou FIX_BUG, enriquece contexto e roteia para o workflow correto.
  Keywords: executar sprint, planejar, fix bug, corrigir erro, iniciar, rodar.
  Ator: sessão principal (orquestrador).
---

# 🧭 STEERING — Auto-Delegate

> Toda mensagem do usuário **deve** passar por este steering antes de ser encaminhada ao Orquestrador.

## 1. CLASSIFICAÇÃO DE INTENÇÃO

| Padrão Detectado na Entrada | Entrypoint |
|---|---|
| `executar sprint`, `rodar sprint`, `execute the sprint`, `iniciar sprint`, `começar sprint`, `build sprint` | `EXECUTE_SPRINT` |
| `planejar`, `plan a sprint`, `criar backlog`, `definir sprint`, `organizar sprint` | `PLAN_SPRINT` |
| `fix bug`, `corrigir bug`, `resolver bug`, `erro`, `defeito`, `bug report`, `não funciona`, `crash`, `exception` | `FIX_BUG` |
| Entrada ambígua ou sem match | → **Solicitar esclarecimento ao usuário** |

Se não mapear com confiança alta, responda:
> "Não consegui classificar sua solicitação. Reformule usando:
> 1. 🚀 execute the sprint <especificação>
> 2. 📋 plan a sprint with the objective <objetivo>
> 3. 🐛 fix bug <descrição do bug>"

## 2. ENRICHMENT LAYER

Antes de rotear, reescreva a entrada em versão canônica e estruturada.
Princípios: explicitar o implícito, estruturar, resolver referências, adicionar critérios de aceite, preservar a intenção e marcar inferências em seção "Inferido".

**EXECUTE_SPRINT:** `## 🎯 Sprint Specification (Enhanced)` com Objetivo Central, Escopo Declarado, Escopo Inferido, Critérios de Aceite Sugeridos e Contexto Original do Usuário (verbatim).

**PLAN_SPRINT:** `## 📋 Sprint Objective (Enhanced)` com Objetivo Central, Resultados Esperados, Entregáveis Sugeridos e Contexto Original do Usuário (verbatim).

**FIX_BUG:** `## 🐛 Bug Report (Enhanced)` com Descrição do Problema, Comportamento Esperado, Comportamento Observado, Severidade Inferida e Contexto Original do Usuário (verbatim).

## 3. FORMATO DE SAÍDA

```json
{
  "steering": {
    "timestamp": "<ISO 8601>",
    "original_input": "<mensagem original, verbatim>",
    "confidence": "<high | medium | low>",
    "requires_clarification": false
  },
  "enhanced_input": { "format": "markdown", "content": "<entrada aprimorada>" },
  "routed_payload": {
    "entrypoint": "<EXECUTE_SPRINT | PLAN_SPRINT | FIX_BUG>",
    "params": { },
    "archiving_protocol": { "mandatory": true }
  }
}
```

Se `requires_clarification` for `true`: `routed_payload` e `enhanced_input` = `null`, adicionar `clarification_message`.

## 4. REGRAS INVIOLÁVEIS

1. **NUNCA** inventar um entrypoint — sem confiança alta, pedir esclarecimento
2. **SEMPRE** preservar a mensagem original do usuário
3. **SEMPRE** incluir o protocolo de archiving nos três entrypoints
4. **NUNCA** pular o enrichment — a entrada aprimorada é obrigatória
5. **NUNCA** executar código, PowerShell ou acessar sistema de arquivos
6. **SEMPRE** produzir output em JSON válido
