# Subtask 20: QA — Validação Final E2E

**Story:** story_07_qa_testes.md
**Tipo:** qa
**Complexidade:** alta
**Agente:** qa-tester-alto

## Descrição

Validação final end-to-end: build, testes, verificação de todos os critérios de aceite
da spec e das stories.

## Arquivos Alvo
- Nenhum (validação apenas)

## Passos
1. Executar build completo (`build_native_now.bat`)
2. Executar todos os testes (`dotnet test`)
3. Validar critérios de aceite da spec (`web_panel_streaming_home.md`):
   - [ ] Browser acessa painel e vê home com categorias
   - [ ] Navega até episódio e vê perfis disponíveis
   - [ ] Vídeo toca no browser via HTML5
   - [ ] Troca de perfil mantém posição (±2s)
   - [ ] Controles funcionam
   - [ ] DLNA cast funciona a partir do web panel
   - [ ] Continue assistindo mostra episódios em progresso
   - [ ] Busca funciona
   - [ ] App WPF: reprodução local e DLNA independentes
   - [ ] URL visível no app
   - [ ] Mobile-first, tema escuro
   - [ ] Fallback HEVC
   - [ ] Regressão DLNA
   - [ ] WatchState persistido no browser mode
4. Verificar regressão: funcionalidades existentes (DLNA cast, web control remoto)

## Critérios de Aceite
- [ ] Build sem erros
- [ ] Todos os testes passam
- [ ] Todos os critérios de aceite da spec validados
- [ ] Relatório final com status por critério

## Dependências
- Todas as subtasks anteriores
