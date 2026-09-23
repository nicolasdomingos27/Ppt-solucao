# Background Presenter

Garante que o passador de slides controle sempre a apresentação (PowerPoint ou
Sumatra PDF), mesmo quando outra janela estiver em foco. App de bandeja para
Windows (C# .NET 8 / WinForms), portátil, sem instalação e sem admin.

## Status

- [x] Etapa 1: protótipo, hook global + Next/Previous via COM
- [x] Etapa 2: Raw Input + filtro pelo passador, config.json, mapeamento completo de teclas
- [x] Extra: Sumatra PDF em tela cheia/modo apresentação (teclas entregues via PostMessage)
- [x] Extra: modo reserva, com as setas do teclado passando slide
- [x] Etapa 3: ícone por estado (verde/amarelo/cinza/vermelho), "Slide X de Y", pausa + Ctrl+Alt+P,
      detecção do passador conectado/desconectado, log com rotação
- [ ] Etapa 4: README.txt de uso no evento
- [ ] Etapa 5: licenciamento (mensal/anual, 2 computadores por licença, 14 dias offline)

## Como o filtro do passador funciona

O hook de teclado pode bloquear teclas, mas não sabe de qual aparelho vieram. O
Raw Input sabe o aparelho, mas não bloqueia. Além disso, um evento bloqueado no
hook não gera Raw Input. Por isso o *apertar* de uma tecla candidata é segurado
e o *soltar* passa; o Raw Input do soltar diz a origem. Se veio do passador,
vira comando; se veio de outro teclado, o par apertar+soltar é reenviado
(SendInput) para a janela em foco. Detalhes em
`src/BackgroundPresenter/KeyRouter.cs`.

## Compilar (Windows, .NET 8 SDK)

```
dotnet run --project src/BackgroundPresenter
```

Gerar o .exe portátil (o GitHub Actions também gera a cada push):

```
dotnet publish src/BackgroundPresenter -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Ícones: `python tools/make_icons.py src/BackgroundPresenter/Resources` (requer Pillow).
