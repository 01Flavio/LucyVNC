# LucyVNC

**LucyVNC** é um projeto de acesso remoto para Windows, desenvolvido em **C# / .NET 8 e WPF**, com implementação do protocolo **RFB (VNC)**.

O objetivo é oferecer uma experiência de gerenciamento e visualização de conexões remotas, mantendo o **LucyVNC Manager/Viewer** separado do **LucyVNC Server**.

> **Status:** em desenvolvimento. O repositório ainda contém um servidor legado executado em primeiro plano pelo aplicativo. A separação completa do Server, seu serviço Windows e o instalador independente fazem parte da evolução planejada; não são funcionalidades concluídas nesta versão.

## Funcionalidades disponíveis

### LucyVNC Manager / Viewer

- Cadastro de computadores por nome, hostname ou endereço IP, porta e grupo opcional.
- Histórico de conexões e preferências locais em SQLite.
- Conexões VNC com autenticação clássica.
- Sessões independentes em abas na janela **Remote Workspace**.
- Controles de conexão, desconexão, modo somente visualização, escala e tela cheia por sessão.
- Reconexão de sessões desconectadas.
- Senhas VNC não são armazenadas no banco local.

### LucyVNC Server — estado atual

O código contém uma biblioteca de servidor RFB autenticado e uma implementação **legada em primeiro plano**, ainda acessível pela seção **Acessos** do aplicativo. Ela é desativada por padrão, exige senha ao iniciar e usa loopback como endereço padrão.

**Arquitetura desejada:** o LucyVNC Server será um componente independente, configurado durante a instalação, sem configurações ou controles de servidor no LucyVNC Manager/Viewer.

## Tecnologias

- C# e .NET 8
- WPF (interface Windows)
- SQLite (cadastros, preferências e histórico)
- RFB/VNC (protocolo de acesso remoto)

## Estrutura do repositório

| Caminho | Responsabilidade |
| --- | --- |
| `src/Vnc.App` | Interface WPF do Manager e Remote Workspace |
| `src/Vnc.Core` | Modelos compartilhados de framebuffer, pixels e conexão |
| `src/Vnc.Protocol` | Negociação RFB, autenticação, mensagens e codificações Raw/CopyRect |
| `src/Vnc.Viewer` | Cliente RFB |
| `src/Vnc.Server` | Biblioteca do servidor RFB |
| `src/Vnc.Platform.Windows` | Captura da área de trabalho e entrada via Windows |
| `src/Vnc.Infrastructure` | Persistência SQLite e migrações |
| `tests/` | Testes de protocolo, servidor, Viewer, integração e persistência |

## Compilar e testar

**Pré-requisitos:** Windows e SDK do .NET 8 com suporte à compilação de projetos WPF.

Na raiz do repositório, execute no PowerShell:

```powershell
dotnet restore "LucyVNC.slnx"
dotnet build "LucyVNC.slnx" -c Release
dotnet test "LucyVNC.slnx" -c Release
```

Para publicar o aplicativo de forma autocontida para Windows x64:

```powershell
dotnet publish "src/Vnc.App/Vnc.App.csproj" -c Release -r win-x64 --self-contained true
```

Executável publicado:

```text
src\Vnc.App\bin\Release\net8.0-windows\win-x64\publish\LucyViewer.exe
```

## Como usar o Manager / Viewer

1. Abra o aplicativo e acesse **Computadores**.
2. Cadastre o destino com hostname ou IP e porta VNC, ou informe o destino diretamente.
3. Digite a senha VNC e conecte-se.
4. Gerencie as conexões em abas na janela **Remote Workspace**.
5. Use os controles de cada aba para ajustar visualização ou desconectar a sessão.

O banco local fica em `%LOCALAPPDATA%\LucyVNC\lucy.db` e armazena computadores, preferências e histórico — **não** senhas VNC.

## Segurança e limitações

- A autenticação VNC clássica **não criptografa** o tráfego e utiliza efetivamente apenas oito caracteres ASCII da senha. Evite exposição direta à internet; prefira uma VPN ou túnel seguro.
- O servidor legado não é um serviço Windows e não configura automaticamente o firewall.
- Controle não assistido, captura da área de trabalho segura, contorno de UAC, TLS/VeNCrypt, codificação Tight, clipboard e captura de múltiplos monitores não estão implementados nesta versão.
- O envio de **Ctrl+Alt+Del** não está disponível.
- Uma aba individual ainda não pode ser destacada para uma janela independente.

## Roadmap

- [ ] Separar completamente o LucyVNC Server do Manager/Viewer.
- [ ] Criar instalação e configuração independentes do LucyVNC Server.
- [ ] Implementar serviço Windows e agente de sessão com comunicação local autenticada.
- [ ] Validar a operação e a interoperabilidade em ambientes Windows reais.
- [ ] Ampliar recursos e segurança do protocolo conforme testes e prioridades.
- [ ] Adicionar seleção de idioma à interface em uma versão futura.

> O roadmap descreve intenções de desenvolvimento, não compromissos de entrega.

## Documentação técnica

- [Instalação e operação](docs/INSTALLATION.md)
- [Arquitetura](docs/ARCHITECTURE.md)
- [Escopo do protocolo](docs/PROTOCOL.md)
- [Interoperabilidade](docs/INTEROPERABILITY.md)

## Contribuições

Sugestões, relatos de bugs e melhorias podem ser discutidos por meio das *Issues* do repositório.

---

**LucyVNC** — projeto em evolução para gerenciamento e acesso remoto via VNC.
