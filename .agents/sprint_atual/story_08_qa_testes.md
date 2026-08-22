# Story 08: QA — Testes Unitários + Validação

## Descrição
Escrever testes unitários para as camadas críticas e validar a integração:
- Testes do WebControlService (comandos, estado, fila)
- Testes do WebSocket handler (parse de comandos, broadcast)
- Testes do modelo WebControlState
- Validação manual: acessar pelo celular e testar todos os controles

## Tipo
qa

## Critérios de Aceite
- [ ] Testes unitários do WebControlService (mock ICastingService)
- [ ] Testes de parse de comandos WebSocket
- [ ] Testes de estado (snapshot, queue)
- [ ] Build passa sem erros
- [ ] Validação manual: painel acessível pelo celular
- [ ] Validação manual: todos os controles funcionam
- [ ] Validação manual: posição atualiza em tempo real
- [ ] Validação manual: tema escuro, mobile-first
