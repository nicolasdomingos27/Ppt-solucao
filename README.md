# PPT Lock

Garante que o passador de slides controle sempre o PowerPoint, mesmo quando outra
janela estiver em foco. App de bandeja para Windows (C# .NET 8 / WinForms), sem instalação.

## Status

- [x] Etapa 1: protótipo, hook global + Next/Previous via COM (ainda sem distinguir o dispositivo)
- [ ] Etapa 2: Raw Input + filtro pelo passador
- [ ] Etapa 3: bandeja completa, config.json, reconexão, log
- [ ] Etapa 4: .exe single-file + README.txt de uso

## Compilar (Windows, .NET 8 SDK)

```
dotnet run --project src/PptLock
```

Gerar o .exe portátil:

```
dotnet publish src/PptLock -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```
