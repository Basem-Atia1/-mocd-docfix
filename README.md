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
git clone https://github.com/Basem-Atia1/-mocd-docfix.git mocd-docfix
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
        repair-<env>-other-services.xlsx   only if you choose every service catalogue
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
  6  Change services
  7  Change environment
  8  Quit
```

Before the menu it asks **which services**: the eight this tool was built for, or every service
catalogue in CRM. The answer governs every mode, is shown in the banner, and is remembered for
that environment. Choosing everything asks CRM what there is and tells you the size of it before
reading a single document — in pre-prod that is over fifty thousand. The other services are kept
in a file of their own, so the one you usually work in stays quick to save.

Start with **Repair run**. It asks whether to use the ledger as it is or update it from CRM
first — updating reads every document in scope, so skip it when you are only carrying on with a
sheet you already have. Then it asks whether to work through the whole file or one document you
name, and how closely you want to watch.

Open `repair-<env>.xlsx`, set the **verdict** column where you disagree with it, save, **close
it**, and run again. The workbook has three tabs — `ledger` is the work still to do, `corrected`
is files waiting to be deleted, `finished` is over. Rows move between them on their own. The two
columns you own:

| verdict | |
|---|---|
| `fix` | work on it |
| `review` | the tool cannot tell; you decide |
| `ignore` | never touch this row |
| `redo` | put this row's record back the way it was |
| `done` | written by the tool when a row is finished |

A document nothing is wrong with never gets a row at all, which is why the sheet is far shorter
than the number of documents. (`skip` used to mean that and is gone; ledgers written before the
change still open, and their `skip` cells read as `done`.)

A document whose record names **no file at all** gets no row either. There is no path to
diagnose and no file to move, so there is nothing the tool could do with one; across every
catalogue in pre-prod they are two thirds of the environment. The scan says how many it found.
Ones an earlier build wrote into the sheet as `review` are taken out of **both** files the next
time the ledger is opened — every time, not only on a run that reads CRM. The file path decides,
not the verdict: a hand can change `review` to anything, and what makes the row impossible to act
on is that there is no file. A row carrying a final state is never removed by any tidy-up.

| final state | |
|---|---|
| *(blank)* | not started |
| `corrected and pending the delete of old docs` | the only value the delete step acts on |
| `old files deleted` | finished |
| `ignore` | keep the old file forever |
| `failed` | see the `error` column and `errors-<env>.txt`; the verdict becomes `review`, so no run retries it until you set it back to `fix` |

Anything else in either column is treated as "leave this row alone" and listed at the end of
the run, so a typo can never cause a write.

The verdict is yours once the row exists — a re-scan never overwrites it. Where a fresh look at
CRM would write something different, the run says so and asks. It asks twice, because those
rows are two different situations: the ones where you have told the tool not to fix something
it wants fixed, and the ones where the tool has learned something since. Anything that would
rewrite a verdict in bulk confirms the count first.

Rows marked `ignore` that no run has touched are offered back the same way: leave them, give
them the verdict the scan makes, or set them to `review` and the tool waits while you type the
answers into the sheet. That is the way back from a fill-down that set a whole column to
`ignore` by accident. Their facts are kept up to date from CRM like any other row — ignoring a
row means do not act on it, not do not look at it. Putting `ignore` in the **final state**
column is different: that closes the row, and nothing looks at it again.

A row the tool finds already correct in CRM — corrected by hand, or by a run whose ledger was
lost — is never uploaded a second time. Every row marked `fix` is checked against CRM once,
before the run starts, and you are shown the list and asked. They settle as `done`, with the
final state saying whether the old file is still on the server and still owed to the delete
step — or left blank when the path never changed and there was nothing to delete. A row whose
path is right but whose file is missing from the server is **not** settled: it keeps `fix` so
the run can put the file back.

A row the ledger calls finished that CRM still files wrongly is reported at the end of the
scan, with the choice of putting it back to `fix`, to `review`, or leaving it alone. Nothing
else would ever mention it.

One row is one document, but one *file* can belong to several. A correction writes to the
`mocd_documentfile` record, and more than one `mocd_document` can point at the same one — so
correcting one row moves the file under every row that shares it.

**Those rows are settled the moment the correction lands**, in the same run, and the run says so
in one line: `3 other row(s) share this file — settled, nothing uploaded for them`. Each settled
row says in its notes which row settled it. They settle as `done` with **no** final state: the
old file belongs to the row that corrected it, and two rows must never queue the same deletion.

Rows whose sibling was corrected by an *earlier* run are found by the check before the run
starts, and settled the same way. The scan also says how many distinct files the sheet holds when
it is fewer than the number of rows.

A document CRM no longer returns has been deleted there, so its row leaves the sheet — quietly,
because there is nothing to decide. The exception is a row that still records work: a correction
whose old file is waiting to be deleted, or a copy left on the file server. Deleting the document
in CRM removes neither, and the row is the only thing that knows where they are, so it stays and
its notes say `[deleted in crm]`.

A row whose *service* you did not scan this run is a different thing — not missing, just not
looked for. Those are left exactly as they are, and counted in one line.

The row number is positional. The sheet is sorted by verdict and renumbered every time it is
written, so a document that was row 1 this morning can be row 408 this afternoon. That is why
every message names the head of the document id beside the row number: the number finds the
line in today's sheet, the id says which document it is.

### Deleting the old files

The step acts on rows saying `corrected and pending the delete of old docs`, and re-checks each
one against CRM immediately before its file goes. A corrected row whose **verdict** you have
changed to `redo` is left alone and counted out loud: deleting the old file is exactly what
makes a redo impossible, so the two steps in that order would destroy the only route back.

A file that is **not there** counts as done, not as a refusal — the outcome the step exists to
reach is that the old file is gone, and it is. Only a server that genuinely refuses is a refusal.

Afterwards it offers to check that the files really went, and separately to check the ones
earlier runs removed — that asks the server once per row, so it says how many first. A file
found still sitting there gets a note on its row, and the **next** delete run lists those rows
and asks whether to remove them. Your final state is never changed behind your back.

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
