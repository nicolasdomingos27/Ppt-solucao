# PPT Lock

Garante que o passador de slides controle sempre o PowerPoint, mesmo quando outra
janela estiver em foco. App de bandeja para Windows (C# .NET 8 / WinForms), sem instalação.

## Status

- [x] Etapa 1: protótipo, hook global + Next/Previous via COM (ainda sem distinguir o dispositivo)
- [x] Etapa 2: Raw Input + filtro pelo passador, config.json, mapeamento completo de teclas
- [x] Extra: Sumatra PDF em tela cheia/modo apresentação (teclas entregues via PostMessage)
- [ ] Etapa 3: bandeja completa (cores, pausa, Ctrl+Alt+P), reconexão, log
- [ ] Etapa 4: .exe single-file + README.txt de uso
- [ ] Etapa 5: licenciamento (mensal/anual, 2 computadores por licença, 14 dias offline)

## Como o filtro do passador funciona

O hook de teclado pode bloquear teclas mas não sabe de qual aparelho vieram; o
Raw Input sabe o aparelho mas não bloqueia, e chega *depois* do hook. Por isso
as teclas candidatas são seguradas por alguns milissegundos até o Raw Input
dizer a origem: do passador viram comando do PowerPoint; de outro teclado são
reenviadas (SendInput) para a janela em foco. Sem resposta em 150 ms, a tecla é
devolvida. Detalhes em `src/PptLock/KeyRouter.cs`.

## Compilar (Windows, .NET 8 SDK)

```
dotnet run --project src/PptLock
```

Gerar o .exe portátil:

```
dotnet publish src/PptLock -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```
