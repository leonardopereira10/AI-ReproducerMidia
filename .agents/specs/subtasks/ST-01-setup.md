# Subtask 01: Setup WPF + Camadas + DI

## Contexto
Spec: `.agents/specs/catra-media-player.md`
Primeira subtask do projeto. Cria a fundação que todas as outras dependem.

## Objetivo
Solução WPF .NET 8+ com estrutura de camadas, DI configurado e build funcional.

## Escopo
### Arquivos a Criar
- `CATRA.sln`
- `src/CATRA.App/` — WPF application (App.xaml, MainWindow.xaml, startup, DI host)
- `src/CATRA.Core/` — Domain models, interfaces, enums (vazio, só estrutura)
- `src/CATRA.Services/` — Business logic (vazio, só estrutura)
- `src/CATRA.Data/` — SQLite, repositories (vazio, só estrutura)
- `src/CATRA.UI/` — Views, ViewModels, Controls, Themes (vazio, só estrutura)
- `tests/CATRA.Core.Tests/` — xUnit test project
- `tests/CATRA.Services.Tests/` — xUnit test project
- `tests/CATRA.Data.Tests/` — xUnit test project
- `.gitignore` (dotnet + native)
- `Directory.Build.props` (shared build config)

### Arquivos NÃO tocar
- `.agents/` — não modificar

## Requisitos Técnicos
- .NET 8+ (LTS), WPF, C# 12
- `Microsoft.Extensions.DependencyInjection` + `Microsoft.Extensions.Hosting`
- `CommunityToolkit.Mvvm` (source generators)
- Referências entre camadas: App → UI → Services → Data → Core
- Core não referencia ninguém (leaf)
- MainWindow com NavigationService básico (frame + back stack)
- App.xaml.cs configura ServiceProvider com serviços registrados
- xUnit + FluentAssertions nos test projects
- `Directory.Build.props` com `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`

## Critérios de Sucesso
- [ ] `dotnet build` passa sem warnings
- [ ] `dotnet test` passa (testes placeholder)
- [ ] App abre MainWindow vazia
- [ ] DI container resolve serviços registrados
- [ ] NavigationService navega entre duas páginas dummy

## Dependências
- Nenhuma (primeira subtask)

## Notas
- Estrutura de pastas dentro de Services: `Library/`, `Playback/`, `Processing/`, `Casting/`, `Storage/`, `Metadata/`
- Não implementar lógica de negócio — só skeleton
- MainWindow deve ter região de conteúdo (Frame) + barra de título custom
