# Subtask 04 — Eventos de fallback tipados no pipeline + UI com dedup por job

| Campo       | Valor                                                |
|-------------|------------------------------------------------------|
| **Story**   | Story 04 — Eventos de fallback tipados no pipeline + UI de processamento com dedup |
| **Tipo**    | dev                                                  |
| **Complexidade** | media                                           |
| **Agente**  | developer-media                                      |
| **Dependências** | Stories 01, 02, 03 (fallbacks reais que esta story reporta) |

## Descrição

A tela de processamento deve informar ao usuário quando o fluxo escolhido não
é o que está sendo executado, sem repetir mensagens. O pipeline passa a
emitir **eventos de fallback tipados** e a UI de processamento exibe **uma
mensagem por tipo por job**.

### Estado atual (investigação read-only)

**Pipeline (`ProcessingPipeline.cs`)** — três pontos de fallback existentes,
nenhum emite evento:
1. **Interp** (linha ~215-234): `CreateInterpolation` em try/catch →
   `NativeBridgeException` → desliga interp e continua. Nenhum sinal para o
   caller além do `Trace.WriteLine`.
2. **Upscale** (linha ~862-882): `CreateUpscalerWithFallback` — FSR4 pedido,
   `NativeBridgeException` → retry com FSR1. Apenas `Trace.WriteLine`.
3. **Encode** (linha ~267): `bridge.CreateEncoder` **sem try/catch e sem
   fallback** — em GPU não-AMD, `NativeBridgeException` propaga e o job
   inteiro falha. (O fallback encoder é responsabilidade da Story 02; esta
   subtask apenas reporta o fallback quando ele existir.)

**Queue Service (`ProcessingQueueService.cs`)** — tem eventos `JobStarted`,
`JobCompleted`, `JobFailed`, `ProgressChanged`. Não tem canal de fallback.
O `IProgress<PipelineProgress>` é a única via de comunicação pipeline→UI.

**ViewModel (`ProcessingQueueViewModel.cs`)** — `ActiveJobItem` tem
`ProgressPct`, `StepLabel`, `ProgressText`. Não tem coleção de fallback
messages. `RefreshActiveJobs()` atualiza a cada `ProgressChanged`.

**UI (`ProcessJobCardControl.xaml`)** — mostra EP + título, progress bar,
step label. Não tem área de mensagens de fallback.

**Core** — sem enum `FallbackType`, sem record `FallbackEvent`. `PipelineProgress`
é um record fechado (7 params); estender com fallback events exigiria novo
tipo ou acoplamento.

### Abordagem

1. **CATRA.Core**: novo enum `FallbackType` + record `FallbackEvent` + evento
   `FallbackOccurred` na interface `IProcessingQueueService`.
2. **Pipeline**: emitir `FallbackEvent` nos 3 pontos de fallback via
   `IProgress<PipelineProgress>` (campo novo nullable) ou callback dedicado.
3. **Queue Service**: repassar eventos de fallback do pipeline para a UI via
   novo evento `FallbackOccurred`.
4. **ViewModel**: deduplicar por `FallbackType` POR JOB (máx 1 mensagem por
   tipo por job). `ActiveJobItem` ganha `ObservableCollection<FallbackMessage>`.
5. **UI**: `ProcessJobCardControl` exibe mensagens de fallback abaixo do
   progress bar, formato "X indisponível → usando Y".
6. **Testes**: dedup por tipo por job.

## Arquivos Alvo (fileScope)

### CATRA.Core — modelo de evento
- `src/CATRA.Core/Processing/FallbackType.cs` — **NOVO**. Enum:
  `Upscale`, `Encoder`, `FrameGen`, `Interp`.
- `src/CATRA.Core/Processing/FallbackEvent.cs` — **NOVO**. Record:
  `FallbackEvent(FallbackType Type, string Requested, string Effective, string Message)`.
  Ex.: `FallbackEvent(FallbackType.Upscale, "FSR4", "FSR1", "FSR4 indisponível → usando FSR1")`.
- `src/CATRA.Core/Interfaces/IProcessingQueueService.cs` — adicionar evento
  `event EventHandler<FallbackEvent>? FallbackOccurred`.

### CATRA.Services — emissão no pipeline
- `src/CATRA.Services/Processing/ProcessingPipeline.cs`:
  - `CreateUpscalerWithFallback` (linha ~862-882): emitir `FallbackEvent`
    quando FSR4→FSR1 ocorrer (catch do `NativeBridgeException`).
  - Bloco de interp (linha ~215-234): emitir `FallbackEvent` quando interp
    é desligada (catch do `NativeBridgeException`).
  - Bloco de encoder (linha ~267): **após** Story 02 implementar fallback
    encoder, emitir `FallbackEvent` aqui. Nesta subtask, apenas preparar o
    ponto de emissão (comentar TODO ou emitir se o fallback já existir).
  - Mecanismo de emissão: via `Action<FallbackEvent>?` injetável no construtor
    (opcional, default null), ou via `IProgress<PipelineProgress>` com campo
    novo. **Decisão**: usar `Action<FallbackEvent>?` injetável — mais limpo,
    não polui `PipelineProgress`.
- `src/CATRA.Services/Processing/ProcessingQueueService.cs`:
  - `ProcessJobAsync`: passar callback de fallback ao pipeline (via construtor
    ou método). Quando pipeline emite `FallbackEvent`, invocar
    `FallbackOccurred?.Invoke(this, fallbackEvent)`.
  - Alternativa: o pipeline já recebe `IProgress<PipelineProgress>` — adicionar
    `IProgress<FallbackEvent>?` como parâmetro opcional de `ProcessAsync` /
    `ProcessBatchAsync`. **Decisão**: `Action<FallbackEvent>?` no construtor
    do pipeline (injetada pelo queue service) — evita mudar a interface
    `IProcessingPipeline`.

### CATRA.UI — ViewModel + View
- `src/CATRA.UI/ViewModels/ProcessingQueueViewModel.cs`:
  - `ActiveJobItem` (linha interna): adicionar
    `ObservableCollection<FallbackMessage> FallbackMessages`.
  - `FallbackMessage` (nested class ou record): `FallbackType Type`,
    `string Text`.
  - Novo handler `OnFallbackOccurred(object? sender, FallbackEvent evt)`:
    dedup por `(jobId, evt.Type)` — usar `HashSet<(int JobId, FallbackType)>`
    no ViewModel para rastrear quais combinações já foram adicionadas.
    Se não existe, cria `FallbackMessage` e adiciona ao
    `ActiveJobItem.FallbackMessages` do job correspondente.
  - `RefreshActiveJobs()`: ao remover job inativo, limpar entradas do HashSet.
  - Subscribe/unsubscribe do evento `FallbackOccurred` no ctor/Dispose.
- `src/CATRA.UI/Controls/ProcessJobCardControl.xaml`:
  - Adicionar `ItemsControl` abaixo do progress bar / step label, bind a
    `FallbackMessages`. Cada item: `TextBlock` com formato
    "⚠ {Text}" (ex.: "⚠ FSR4 indisponível → usando FSR1").
  - Visibilidade: Collapsed quando `FallbackMessages.Count == 0`.
- `src/CATRA.UI/Controls/ProcessJobCardControl.xaml.cs` — sem mudanças
  (DataContext já é `ActiveJobItem`).

### Testes
- `tests/CATRA.Services.Tests/Processing/ProcessingPipelineTests.cs`:
  - Teste: FSR4 create falha → `FallbackEvent` emitido com
    `Type=Upscale, Requested="FSR4", Effective="FSR1"`.
  - Teste: interp create falha → `FallbackEvent` emitido com
    `Type=Interp, Requested="rife", Effective="none"`.
  - Teste: sem fallback → nenhum `FallbackEvent` emitido.
- `tests/CATRA.UI.Tests/ProcessingQueueViewModelTests.cs`:
  - Teste: 2 fallbacks do mesmo tipo para o mesmo job → apenas 1 mensagem
    na coleção (dedup).
  - Teste: fallbacks de tipos diferentes para o mesmo job → 2 mensagens.
  - Teste: fallback do mesmo tipo para jobs diferentes → 1 mensagem por job.
  - Teste: job removido (completo) → mensagens limpas.

## Passos

### Passo 1 — Modelo em CATRA.Core

1. Criar `src/CATRA.Core/Processing/FallbackType.cs`:
   ```csharp
   namespace CATRA.Core.Processing;
   public enum FallbackType
   {
       Upscale,
       Encoder,
       FrameGen,
       Interp,
   }
   ```
2. Criar `src/CATRA.Core/Processing/FallbackEvent.cs`:
   ```csharp
   namespace CATRA.Core.Processing;
   public sealed record FallbackEvent(
       FallbackType Type,
       string Requested,
       string Effective,
       string Message);
   ```
3. Adicionar evento em `IProcessingQueueService`:
   ```csharp
   event EventHandler<FallbackEvent>? FallbackOccurred;
   ```

### Passo 2 — Emissão no Pipeline

1. Adicionar parâmetro `Action<FallbackEvent>? onFallback = null` no
   construtor do `ProcessingPipeline`. Guardar em campo `_onFallback`.
2. Em `CreateUpscalerWithFallback` (catch `NativeBridgeException`):
   ```csharp
   _onFallback?.Invoke(new FallbackEvent(
       FallbackType.Upscale, "FSR4", "FSR1",
       "FSR4 indisponível → usando FSR1"));
   ```
3. No bloco de interp (catch `NativeBridgeException`):
   ```csharp
   _onFallback?.Invoke(new FallbackEvent(
       FallbackType.Interp, config.InterpMethod, "nenhum",
       $"{config.InterpMethod} indisponível → sem interpolação"));
   ```
4. Encoder: preparar ponto (TODO comentado ou emissão se Story 02 já
   implementou fallback).

### Passo 3 — Repasse no QueueService

1. No construtor do `ProcessingQueueService`: receber o pipeline concreto
   (ou factory) e injetar callback de fallback. Como o pipeline é injetado
   via `IProcessingPipeline` (interface), e o callback é do pipeline concreto:
   - Opção A: cast para `ProcessingPipeline` e setar propriedade.
   - Opção B: adicionar `Action<FallbackEvent>?` como parâmetro do
     `ProcessingPipeline` construtor; QueueService cria o pipeline com o
     callback.
   - **Decisão**: como o pipeline é criado via DI (Transient), o QueueService
     não o constrói diretamente. Melhor: expor `Action<FallbackEvent>?` como
     propriedade setável no `ProcessingPipeline`, e o QueueService seta no
     `ProcessJobAsync` antes de chamar `ProcessAsync`.
   - **Alternativa mais limpa**: adicionar `IProgress<FallbackEvent>?` como
     parâmetro opcional em `IProcessingPipeline.ProcessAsync`. Porém muda
     interface. **Usar**: propriedade `OnFallback` no `ProcessingPipeline`.
2. No `ProcessJobAsync`, antes de `_pipeline.ProcessAsync(...)`:
   ```csharp
   if (_pipeline is ProcessingPipeline concrete)
   {
       concrete.OnFallback = evt => FallbackOccurred?.Invoke(this, evt);
   }
   ```

### Passo 4 — ViewModel com dedup

1. Adicionar nested class `FallbackMessage` no `ProcessingQueueViewModel`:
   ```csharp
   public sealed partial class FallbackMessage : ObservableObject
   {
       public FallbackType Type { get; init; }
       [ObservableProperty] private string _text = string.Empty;
   }
   ```
2. Adicionar em `ActiveJobItem`:
   ```csharp
   public ObservableCollection<FallbackMessage> FallbackMessages { get; } = [];
   ```
3. No ViewModel, campo de dedup:
   ```csharp
   private readonly HashSet<(int JobId, FallbackType)> _seenFallbacks = new();
   ```
4. Handler `OnFallbackOccurred`:
   ```csharp
   private void OnFallbackOccurred(object? sender, FallbackEvent evt) => Post(() =>
   {
       // Encontrar o ActiveJobItem do job atual (fallback não carrega jobId;
       // usar CurrentJob ou ActiveJobs).
       // Para cada active job, verificar se (job.Id, evt.Type) já foi visto.
       // Se não, adicionar FallbackMessage e marcar no HashSet.
       foreach (var job in _queue.ActiveJobs)
       {
           var key = (job.Id, evt.Type);
           if (_seenFallbacks.Add(key))
           {
               var item = ActiveJobItems.FirstOrDefault(i => i.JobId == job.Id);
               item?.FallbackMessages.Add(new FallbackMessage
               {
                   Type = evt.Type,
                   Text = evt.Message,
               });
           }
       }
   });
   ```
   **Nota**: o `FallbackEvent` não carrega jobId. Alternativa: adicionar
   `int JobId` ao `FallbackEvent` (mais preciso). Ou: o callback é setado
   por-job no QueueService, então o jobId é conhecido no momento do invoke.
   **Decisão**: adicionar `int JobId` ao `FallbackEvent` para precisão.
5. Subscribe no construtor: `_queue.FallbackOccurred += OnFallbackOccurred;`
6. Unsubscribe no Dispose.
7. Em `RefreshActiveJobs()`, ao remover job inativo:
   ```csharp
   _seenFallbacks.RemoveWhere(f => f.JobId == removedJobId);
   ```

### Passo 5 — UI exibe mensagens

1. Em `ProcessJobCardControl.xaml`, após o `DockPanel` do step label:
   ```xml
   <ItemsControl ItemsSource="{Binding FallbackMessages}"
                 Margin="0,4,0,0">
       <ItemsControl.ItemTemplate>
           <DataTemplate>
               <TextBlock Foreground="{DynamicResource WarningBrush}"
                          Text="{Binding Text, StringFormat='⚠ {0}'}"
                          TextWrapping="Wrap"
                          FontSize="12" />
           </DataTemplate>
       </ItemsControl.ItemTemplate>
   </ItemsControl>
   ```
2. Visibilidade: usar `Style.Triggers` com `DataTrigger` em
   `FallbackMessages.Count == 0` → `Collapsed`, ou converter via
   `BooleanToVisibilityConverter` sobre `FallbackMessages.Count`.

### Passo 6 — Testes

1. **Pipeline**: mock `INativeBridge` para lançar `NativeBridgeException`
   no `CreateUpscaler` (FSR4) → verificar `FallbackEvent` emitido com
   `Type=Upscale, Requested="FSR4", Effective="FSR1"`.
2. **Pipeline**: mock `INativeBridge` para lançar no `CreateInterpolation` →
   verificar `FallbackEvent` com `Type=Interp`.
3. **Pipeline**: sem exceções → verificar nenhum `FallbackEvent` emitido.
4. **ViewModel dedup**: enviar 3 `FallbackEvent(Upscale)` para mesmo job →
   `FallbackMessages.Count == 1`.
5. **ViewModel dedup**: enviar `FallbackEvent(Upscale)` + `FallbackEvent(Interp)`
   para mesmo job → `FallbackMessages.Count == 2`.
6. **ViewModel dedup**: enviar `FallbackEvent(Upscale)` para job A e job B →
   cada `ActiveJobItem` tem 1 mensagem.
7. **ViewModel cleanup**: job completado (removido de ActiveJobItems) →
   entradas do HashSet limpas.

## Critérios de Aceite

- [ ] **CA-4.1** — Enum `FallbackType` existe em CATRA.Core com valores
      mínimos: `Upscale`, `Encoder`, `FrameGen`, `Interp`.
- [ ] **CA-4.2** — Record `FallbackEvent` existe em CATRA.Core com campos:
      `Type`, `Requested`, `Effective`, `Message`, `JobId`.
- [ ] **CA-4.3** — `ProcessingPipeline` emite `FallbackEvent` quando
      FSR4→FSR1 fallback ocorre (upscale).
- [ ] **CA-4.4** — `ProcessingPipeline` emite `FallbackEvent` quando
      interpolação é desligada por falha (interp).
- [ ] **CA-4.5** — `ProcessingQueueService` repassa eventos via
      `FallbackOccurred`.
- [ ] **CA-4.6** — `ProcessingQueueViewModel` deduplica: máximo 1 mensagem
      por `FallbackType` por job.
- [ ] **CA-4.7** — `ProcessJobCardControl` exibe mensagens de fallback
      visíveis na tela, formato "X indisponível → usando Y".
- [ ] **CA-4.8** — Job sem fallback não exibe mensagens (UI limpa).
- [ ] **CA-4.9** — Testes de dedup passam (3 cenários mínimos).
- [ ] **CA-4.10** — Testes de emissão no pipeline passam (3 cenários mínimos).
- [ ] **CA-4.11** — Testes existentes passam (build gate).

## Notas

- O `FallbackEvent` carrega `JobId` para precisão na dedup (o callback é
  setado pelo QueueService que conhece o job em processamento).
- A propriedade `OnFallback` no `ProcessingPipeline` é a forma mais limpa
  de injetar o callback sem mudar a interface `IProcessingPipeline`. O
  QueueService faz cast para o tipo concreto (padrão já usado internamente).
- `WarningBrush` pode já existir nos recursos da UI; se não, usar
  `SectionHeaderForegroundBrush` ou adicionar novo resource.
- O fallback de encoder (AMF→x265) depende da Story 02. Esta subtask prepara
  o ponto de emissão mas não implementa o fallback em si.
- FrameGen (FSR3 FG→RIFE) ainda não está integrado no pipeline offline
  (FG é playback-only, `catra_fg`). O enum já inclui `FrameGen` para
  quando a integração ocorrer.
