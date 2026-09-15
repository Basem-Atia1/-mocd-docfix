# docfix

Repairs MoCD document file paths: finds documents filed under the wrong service catalogue,
re-uploads them under the right one, updates CRM, and removes the old files — one ledger,
one row per document, nothing done without a record of it.

## What you need before you start

- **Windows.** Secrets use DPAPI, CRM uses NTLM, and the tool opens files with the shell.
- **.NET 8 SDK**, to build it.
- **The client VPN, connected.** Without it neither CRM nor the file server resolves, and the
  tool stops with "No such host is known".
- **Your own CRM account** and **your own file-server API key**. See below — you will not be
  given anybody else's, and the tool has no way to use them.

## Setting up

```
git clone https://github.com/<owner>/mocd-docfix.git
cd mocd-docfix
publish.cmd
bin\docfix\docfix.exe
```

`publish.cmd` runs the tests and refuses to publish if any fail, so a broken build never
reaches the folder you run.

On first run the tool asks which environment you want and, for one it has never seen, asks for:

| | |
|---|---|
| File service base URL | e.g. `http://<file-server>:83` |
| CRM URL | e.g. `https://<crm-host>/MoCD` |
| CRM domain and user | **yours** |
| CRM password | **yours** |
| File service API key | the `Apikey` header value |

That is the whole setup. Everything after it is remembered.

## Where your credentials go

```
%APPDATA%\mocd-docfix\
    config.json     URLs, your CRM user name, the service catalogues — plain text
    secrets.dat     your CRM password and API key — encrypted
```

**Nothing here is in the repository, and nothing here can be shared.** `secrets.dat` is
encrypted with Windows DPAPI and keyed to the account that wrote it: copied to another machine
or opened under another Windows account it simply cannot be decrypted, and the tool says so and
asks you to enter your own.

So every person who runs docfix uses their own CRM account, and nobody can reach anybody
else's. That is deliberate — the file server and CRM both log who did what, and a shared
account would make that record worthless.

If you ever hand somebody the built `bin\docfix` folder rather than this link: it contains no
credentials, and they will be asked for their own on first run. Do not send
`%APPDATA%\mocd-docfix\` with it.

## Where your work goes

```
D:\mocd-docfix-data\               (change DataRoot in config.json if you have no D: drive)
    reports\<env>\
        repair-<env>.xlsx          THE LEDGER — you edit this one
        repair-<env>.csv           a plain-text copy the tool maintains; editing it does nothing
        changes-<env>.jsonl        every change made to CRM, appended, never rewritten
        errors-<env>.txt           what failed, in full
        previous\                  the last three copies of the ledger
    backup\<env>\<file>__<doc id>\
        old\  new\  crm.json       the bytes and the CRM record as they were
```

The ledger is the whole account of the work. The backup folder is the route back.

**Do not delete anything under `reports\` or `backup\`.** Redo reads `crm.json`; the delete step
reads the ledger; the journal is what repairs the ledger if a run is cut short.

## Using it

```
  1  Repair run                  build the ledger, then work through it
  2  Delete old files            the old files of corrected rows
  3  Redo                        put records back the way they were
  4  Check it all                confirm the old files really are gone
  5  Is this file still there?   one path or documentfile id
  6  Change environment
  7  Quit
```

Start with **Repair run**. It reads every document in the configured services and writes the
ledger, then asks whether to work through the whole file or one document you name, and how
closely you want to watch.

Open `repair-<env>.xlsx`, set the **verdict** column where you disagree with it, save, **close
it**, and run again. The two columns you own:

| verdict | |
|---|---|
| `fix` | work on it |
| `review` | the tool cannot tell; you decide |
| `skip` | already correct, or no file |
| `ignore` | never touch this row |
| `redo` | put this row's record back the way it was |
| `done` | written by the tool when a row is corrected |

| final state | |
|---|---|
| *(blank)* | not started |
| `corrected and pending the delete of old docs` | the only value the delete step acts on |
| `old files deleted` | finished |
| `ignore` | keep the old file forever |
| `failed` | see the `error` column and `errors-<env>.txt` |

Anything else in either column is treated as "leave this row alone" and listed at the end of
the run, so a typo can never cause a write.

### Stopping

- **Watch** asks after every document.
- **Quiet** and **Unattended**: press `q` at any time. The document in progress is always
  finished and recorded first; nothing after it is started.
- Any failure stops and asks, in every mode.

## What it will not do

- It never writes to Azure DevOps, and no longer reads from it.
- It never creates a `mocd_documentfile` and never repoints a document: a correction updates
  the record the document already points at.
- It never deletes a file the ledger has not recorded as corrected, and never one another
  record still refers to.

## Running one step directly

```
docfix repair   --env dev
docfix delete   --env dev
docfix redo     --env dev
docfix check    --env dev
docfix config   --env dev     set up or change an environment
```

`--dry-run` reads and reports without writing. `--confirm-production` is required before
production can be selected at all.
