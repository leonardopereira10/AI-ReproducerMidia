---
name: testing-patterns
description: |
  Padrões de teste xUnit/FluentAssertions/Moq para RazeGas. Carregar ao escrever
  testes unitários, de validação ou de serviço. Define naming conventions (AAA),
  estrutura de arquivos, factories de dados, anti-patterns e metas de coverage.
  Keywords: teste, xUnit, mock, FluentAssertions, validar, coverage, unit test,
  escrever teste, QA, TDD.
  Ator: qa-tester-*, developer-* (ao escrever testes).
---

# Testing Patterns — RazeGas Backend (.NET 10.0)

## Environment

- **Test Framework:** xUnit (`[Fact]`, `[Theory]`, `[InlineData]`)
- **Assertions:** FluentAssertions (`Should().Be()`, `Should().BeNull()`, etc.)
- **Mocking:** Moq (`Mock<T>`, `Setup()`, `Returns()`)
- **Test Project:** `RazeGas.Tests/`
- **Build/Test Commands:** → `.agents/project/build-commands.md` — comandos de build, test, run e format do RazeGas Backend.

---

## Naming Conventions

### Entity Tests

| Regra | Exemplo |
|-------|---------|
| Arquivo | `RazeGas.Tests/Entities/{NomeEntidade}Tests.cs` |
| Classe | `public class {NomeEntidade}Tests` |
| Método | `Given_{Contexto}_When_{Acao}_Then_{Resultado}()` |

**Exemplos:**
```
RazeGas.Tests/Entities/ClienteTests.cs
RazeGas.Tests/Entities/PedidoTests.cs
RazeGas.Tests/Entities/EntregaTests.cs
```

### Validator Tests

| Regra | Exemplo |
|-------|---------|
| Arquivo | `RazeGas.Tests/Validators/{NomeEntidade}ValidatorTests.cs` |
| Classe | `public class {NomeEntidade}ValidatorTests` |
| Método | `Given_{ValidEntityWithInvalidField}_WhenValidate_ThenShouldHaveError()` |

**Exemplos:**
```
RazeGas.Tests/Validators/ClienteValidatorTests.cs
RazeGas.Tests/Validators/PedidoValidatorTests.cs
```

### Service Tests

| Regra | Exemplo |
|-------|---------|
| Arquivo | `RazeGas.Tests/Services/{NomeServico}Tests.cs` |
| Classe | `public class {NomeServico}Tests` |
| Método | `Given_{Setup}_{When}_{Then}()` |

---

## Structure — AAA Pattern

Cada teste deve seguir **Arrange → Act → Assert** com comentários explícitos:

```csharp
[Fact]
public void Given_ValidCliente_WhenCreate_ThenShouldInitializeCorrectly()
{
    // Arrange
    var id = Guid.NewGuid();

    // Act
    var cliente = new Cliente { Id = id, Nome = "João Silva" };

    // Assert
    cliente.Id.Should().Be(id);
    cliente.Nome.Should().Be("João Silva");
}
```

### Entity Test Checklist

| Categoria | O que testar |
|-----------|-------------|
| **Inicialização** | Propriedades padrão, valores defaults |
| **Relacionamentos** | Collections inicializadas vazias, Add funciona |
| **Nullability** | Propriedades nullable aceitam null |
| **Tipo enums** | Valores válidos e inválidos |
| **Data/Cálculos** | Timestamps, cálculos automáticos |

### Validator Test Checklist

| Categoria | O que testar |
|-----------|-------------|
| **Input válido** | `IsValid == true` para dados corretos |
| **Null** | Propriedades `[Required]` com null → erro |
| **Empty string** | Propriedades `[Required]` com `""` → erro |
| **Max length** | Exceder `MaxLength` → erro |
| **Formato** | CPF/CNPJ/Email/Telefone inválidos → erro |
| **Enum inválido** | Valor fora do enum → erro |
| **TestValidate** | `ShouldNotHaveValidationErrorFor` para campos válidos |

---

## FluentAssertions Cheat Sheet

```csharp
// Valores
value.Should().Be(expected);
value.Should().NotBe(unexpected);
value.Should().BeNull();
value.Should().NotBeNull();
value.Should().BeTrue();
value.Should().BeFalse();
value.Should().BeInRange(min, max);
value.Should().BeAfter(other);
value.Should().BeBefore(other);

// Collections
collection.Should().BeEmpty();
collection.Should().NotBeEmpty();
collection.Should().ContainSingle();
collection.Should().Contain(item => item.Id == expectedId);
collection.Should().ContainInOrder(items);

// Objects
obj.Should().BeEquivalentTo(expected);
obj.Should().BeOfType<ExpectedType>();

// Exceptions
Action action = () => subject.DoSomething();
action.Should().Throw<ExpectedException>()
      .WithMessage("*expected message*");

// String
str.Should().BeNullOrEmpty();
str.Should().NotBeNullOrEmpty();
str.Should().StartWith("prefix");
str.Should().EndWith("suffix");
```

---

## Moq Cheat Sheet

```csharp
// Setup
var mock = new Mock<IRepository<T>>();
mock.Setup(r => r.GetById(id)).Returns(entity);

// Async
mock.Setup(r => r.InsertAsync(entity))
    .Returns(Task.FromResult(true));

// Verify
mock.Verify(r => r.InsertAsync(It.IsAny<T>()), Times.Once());
mock.Verify(r => r.GetById(id), Times.Once());

// Throws
mock.Setup(r => r.GetById(id)).Throws<KeyNotFoundException>();

// Callbacks
mock.Setup(r => r.Delete(id))
    .Callback<Guid>(idToDelete => deletedIds.Add(idToDelete));
```

---

## Test Data Patterns

### Valid Entity Factories (inline)

```csharp
// Para testes de entity — dados mínimos válidos
var cliente = new Cliente
{
    Nome = "João Silva",
    Documento = "52948227825",      // CPF válido
    TipoDocumento = EnumTipoDocumento.PF,
    Telefone = "(11) 99999-9999",
    Status = EnumStatusGenerico.Ativo
};
```

### Invalid Data Generators

```csharp
// CPF inválido (todos dígitos iguais)
var invalidCpf = "11111111111";

// CNPJ inválido (todos dígitos iguais)
var invalidCnpj = "11111111111111";

// String excedendo MaxLength
var tooLong = new string('x', 201);  // se MaxLength = 200

// Email inválido
var invalidEmail = "invalid-email";
```

---

## Anti-Patterns em Testes

| Anti-Pattern | Por que evitar | Correção |
|-------------|---------------|----------|
| Teste com mais de 3 Asserts | Dificulta identificar qual falhou | Dividir em múltiplos `[Fact]` |
| Teste dependente de ordem | Falha intermitente em CI | Cada teste deve ser independente |
| Mock excessivo | Testa o mock, não o código | Mockar apenas dependências externas |
| Teste sem AAA comments | Difícil entender intenção | Sempre usar `// Arrange`, `// Act`, `// Assert` |
| Dados hardcoded sem contexto | `Nome = "test"` não explica o teste | Usar nomes descritivos |
| `[Theory]` com muitos InlineData | Arquivo gigante | Extrair para `ClassData` ou `MemberData` |

---

## Coverage Meta

| Camada | Meta mínima |
|--------|-----------|
| Entities | 80% (propriedades, relacionamentos, defaults) |
| Validators | 90% (todos os campos, null, empty, max, formato) |
| Services | 70% (caminho feliz + erros de negócio) |
| Controllers | 50% (endpoints CRUD básicos) |

---

## Cross-References

- **Build Gate:** `.agents/skills/build-gate/SKILL.md`
- **Testing Conventions:** `.agents/project/testing-conventions.md`

## Version

- **v1.0** — 2026-07-07 — Initial testing patterns for RazeGas Backend
