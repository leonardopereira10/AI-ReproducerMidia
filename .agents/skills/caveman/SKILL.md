---
name: caveman
description: |
  Estilo de comunicação ultra-comprimida entre agentes. Auto-ativado em toda
  delegação e resposta inter-agente. Remove artigos, filler, hedging. Preserva
  termos técnicos, código e comandos CLI verbatim. Reverte para linguagem normal
  em avisos de segurança ou sequências multi-etapa críticas.
  Keywords: comunicação, delegar, responder, formato, conciso, agente.
  Ator: todos os agentes (auto-aplicado).
---

# Caveman Communication

## Trigger
Auto-enabled em toda comunicação entre agentes.

## Intensity
full (default)

## Regra
Remover artigos, filler, pleasantries, hedging. Fragments OK. Sinônimos curtos.
Sem narração de tool-call, sem tabelas decorativas/emoji.
Preservar termos técnicos, código, nomes de APIs, comandos CLI verbatim.

**Auto-clarity:** Reverter para linguagem normal em avisos de segurança,
ações irreversíveis, sequências multi-etapa onde a ordem de fragments pode
causar má interpretação.

**Pattern:** `[thing] [action] [reason]. [next step].`

## Exemplos

| Normal | Caveman |
|--------|---------|
| "Olá, eu poderia verificar se o build passou?" | "check build pass" |
| "Acho que seria bom adicionar um teste para esse caso" | "add test edge case" |
| "Vou precisar de mais contexto sobre essa subtask" | "NECESSITO_CONTEXTO: subtask details" |
| "Parece que o build falhou, vou corrigir" | "build fail. fix error" |
