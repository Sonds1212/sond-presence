# Sond Client — serviço (presença + atualizações)

Um único serviço faz duas coisas:

1. **Presença**: faz o selo do client aparecer para outros jogadores **em qualquer servidor**,
   inclusive nos que você não controla (BawMC, etc.). Mesma ideia do Lunar/Feather: o
   servidor de Minecraft não participa, quem junta as pontas é este serviço. O estado fica no **Redis**.
2. **Atualizações**: guarda as versões publicadas do launcher e do mod. Todo launcher instalado
   confere a cada 30 min e se atualiza sozinho.

> ⚠️ O `appsettings.json` desta pasta tem a **senha do Redis** e o **token de admin**.
> Ele fica só no servidor e nunca vai para os jogadores. Não compartilhe esta pasta.

## Presença: como funciona

1. A cada 20 s, o mod manda `POST /presence` com `{ uuid, name, server }`.
2. O serviço grava no Redis e devolve quem mais está **no mesmo servidor** usando o client.
3. O mod marca na tab list todo mundo que veio nessa lista.

Chaves no Redis (prefixo `Redis:Prefix`, padrão `sond:`):

| Chave | Tipo | Conteúdo |
|---|---|---|
| `sond:user:{uuid}` | hash | `name`, `server`; expira sozinha após o TTL |
| `sond:srv:{servidor}` | sorted set | uuid → horário do último heartbeat |
| `sond:online` | sorted set | todo mundo online (para o `/stats`) |
| `sond:servers` | set | servidores com alguém (para o `/stats`) |

Quem fecha o jogo sem avisar some sozinho depois de `TtlSeconds` (45 s). Como o estado está
no Redis, reiniciar o serviço (ou rodar mais de uma instância) não perde ninguém.

Sem `Redis:ConnectionString` o serviço usa memória: serve para testar local.

## Rodar

O serviço precisa **alcançar o Redis** (`10.0.55.1:10009`, rede privada) **e** ser acessível
pelos jogadores pela internet. Então ele roda numa máquina da mesma rede do Redis, com uma porta
pública (ou atrás de um proxy reverso com HTTPS).

```bash
cd sond-presence
dotnet publish -c Release -r linux-x64 --self-contained true -o out
./out/SondPresence
```

No Windows, troque `linux-x64` por `win-x64`. Sobe em `http://0.0.0.0:8080` (mude em `Kestrel:Endpoints:Http:Url`).
Os arquivos publicados ficam em `updates/` ao lado do executável: mantenha essa pasta entre deploys.

## Configurar

`appsettings.json` (ou variáveis de ambiente, ex.: `Redis__ConnectionString`):

| Chave | Para que serve |
|---|---|
| `Database:ConnectionString` | MySQL/TiDB (ex.: TiDB Cloud). Tem prioridade sobre o Redis; a tabela é criada sozinha |
| `Database:Table` | Nome da tabela (padrão `sond_presence`) |
| `Redis:ConnectionString` | Redis (`host:porta,password=...`), usado se `Database` estiver vazio. Os dois vazios = memória |
| `Redis:Prefix` | Prefixo das chaves (padrão `sond:`) |
| `Presence:TtlSeconds` | Segundos que uma presença vale sem heartbeat (padrão 45) |
| `Presence:ApiKey` | Se preenchido, o mod precisa mandar o mesmo valor em `X-Sond-Key` |
| `Updates:Directory` | Onde guardar as versões publicadas (padrão `updates/`) |
| `Updates:AdminToken` | Token para publicar. **O mesmo** do `publish.config.json` na raiz do projeto |
| `Cosmetics:Admins` | Nicks que podem dar/tirar cores do ícone (padrão `SondS_`; vários separados por vírgula) |
| `Cosmetics:TokenSecret` | Opcional. Segredo das sessões de cosméticos; sem ele, reiniciar o serviço só pede login de novo |

## Cosméticos (cor do ícone na TAB)

- `GET /cosmetics/user/{nick}`: cores que o jogador tem e a equipada (público).
- `GET /cosmetics/challenge` + `POST /cosmetics/auth`: login pela conta Minecraft — o mod avisa a Mojang
  (como ao entrar num servidor original) e o serviço confere em `hasJoined`. Devolve um token de 6 h.
- `POST /cosmetics/equip` (header `X-Sond-Session`): equipa uma cor que o jogador tem.
- `POST /cosmetics/admin/grant` e `/cosmetics/admin/revoke`: só para os nicks de `Cosmetics:Admins`.
- O heartbeat de presença devolve `colors` (uuid → `RRGGBB`) e o mod pinta o selo de cada um.

Tabelas (criadas sozinhas): `sond_cosmetic_colors` e `sond_cosmetic_equipped`. Na primeira vez, as cores
que eram fixas no mod viram cores dadas e equipadas (SondS_ `#A8284D`, kistzinho `#2F0B1D`).

O endereço público do serviço vai em `publish.config.json` (`serviceUrl`), na raiz do projeto.
O `build.ps1` embute esse endereço no launcher, e o launcher configura o mod de cada instância
sozinho (`presenceUrl` em `config/sondclient/settings.json`). Ninguém precisa editar nada à mão.

## Endpoints

| Método | Rota | O que faz |
|---|---|---|
| POST | `/presence` | Heartbeat; devolve `{ users, ttlSeconds }` com quem está no mesmo servidor |
| POST | `/presence/leave` | Remove a presença na hora (o mod chama ao sair) |
| GET | `/stats` | Total online e quantos por servidor (o launcher mostra o total) |
| GET | `/download` | Link fixo para o instalador mais recente: mande esse para os amigos |
| GET | `/updates/manifest.json` | Manifesto assinado da versão atual |
| GET | `/updates/files/{sha256}/{nome}` | Arquivos das versões |
| PUT | `/admin/files/...`, `/admin/manifest` | Publicação (exige `X-Sond-Admin`) |

## Segurança

- **Atualizações são assinadas** (ECDSA P-256). A chave privada fica só no seu PC
  (`%USERPROFILE%\.sond\update-signing-key.pem`) e o launcher só aceita manifesto assinado
  por ela. Mesmo que alguém invada este servidor ou descubra o token de admin, não consegue
  empurrar um exe falso para os jogadores. **Faça backup dessa chave**: sem ela, os launchers
  instalados não aceitam mais atualizações e todo mundo teria que reinstalar.
- O Redis **nunca** é exposto aos jogadores: só este serviço conversa com ele.
- Use HTTPS se puder (proxy reverso). Com a assinatura, a atualização é segura mesmo em HTTP,
  mas UUID e servidor da presença viajam em texto puro.
- Ninguém prova que é dono do UUID que envia. Para um selo cosmético isso é aceitável; se um dia
  virar algo com valor, aí precisa validar a sessão do jogador com a Mojang.
