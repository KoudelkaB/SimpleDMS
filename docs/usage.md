# Používání

## Jednorázové nastavení synchronizované složky

1. Nainstalujte [Google Drive for desktop](https://www.google.com/drive/download/)
   a přihlaste se účtem, se kterým je archiv sdílen. Drive se připojí jako
   disk G:. Na Linuxu použijte Insync nebo rclone (`rclone bisync`, případně
   `rclone mount` s `--vfs-cache-mode full`).
2. Je-li archiv sdílený od jiného účtu, přidejte jej na webu Google Drive do
   Můj disk (pravé tlačítko → Uspořádat → Přidat zástupce do Můj disk).
3. Chcete-li archiv používat bez internetu, v Průzkumníku u složky archivu
   zvolte pravé tlačítko → Offline přístup → Dostupné offline.

## Otevření a přidání

Na kartě Archiv vyberte složku, ve které leží registr, např.
`G:\Můj disk\Databáze dokumentů`. SimpleDMS najde dvojici
`Jméno archivu.xlsx` a `Jméno archivu/`; starší složka s podtržítky místo
mezer je podporována. Jedinou dvojici otevře automaticky, více dvojic nabídne
k výběru. Nový archiv založíte zadáním jména.

Na kartě Přidat dokument zvolte kategorii (názvy pocházejí z listu
„Kódování dokumentů“), vyplňte název a případně autora (nabízí dříve
použitá jména), referenci, platnost (výběr data), poznámky a přílohy.
Aplikace ukazuje číslo, které bude přiděleno. Přílohy jsou soubory (vybrané
nebo přetažené), složky ani podsložky se nepřidávají. Stejně jako v původní
evidenci je dokument buď jeden soubor `číslo.přípona` (např. `100242.pdf`),
nebo při více souborech složka `číslo`. Uložení zkopíruje přílohy do složky
dokumentů, zapíše řádek do registru (M a N jako odkazy stejně jako v původní
evidenci) a připraví štítek. Bez příloh vznikne papírový záznam. Doplnění
příloh v detailu zachová původní číslo: papírový záznam dostane soubor,
dokument tvořený jedním souborem se změní na složku `číslo`, do které se
původní soubor přesune spolu s novými.

Používejte jednoho zapisujícího správce. Čtenáři si mohou zapnout režim
pouze pro čtení v Nastavení. Čísla jsou stabilní a nikdy nevyplňují mezery;
soubor nebo složka začínající číslem, které v registru ještě není, své číslo
také blokuje. Před každou změnou registru vzniká místní záloha (posledních
50). Je-li registr otevřený v Excelu, zápis se odmítne; zavřete Excel a
zkuste to znovu.

## Volitelné Google ID

Na kartě Archiv lze vložit odkaz na složku Google Drive, ve které leží
registr, a přihlásit se. Aplikace pak každých 5 minut doplní do Q Google ID
složek, které synchronizační klient už nahrál. Odkaz v M i QR kód na štítku
tak vedou přímo na Google Drive. Štítek vytištěný dříve, než je ID známé,
nese v QR evidenční číslo. Bez propojení archiv funguje stejně, jen se Q nedoplňuje.

## Rozpracovanost

Rozpracované záznamy mají oranžový celý řádek A–Q (#FFC000); jiný
příznak se nepoužívá. Světle oranžová jednotlivá buňka se za rozpracovanost
nepovažuje. Původní styly si aplikace uchovává ve skryté části XLSX pro
návrat po dokončení. Změna stavu nemění evidenční číslo. Sloupec R
„Stav zpracování“, který zapisovaly dřívější verze SimpleDMS, se při otevření
archivu z registru odstraní.

## Offline

Aplikace pracuje přímo se synchronizovanou složkou, takže funguje bez
internetu stejně jako online; změny nahraje synchronizační klient po připojení.
Není-li složka vůbec dostupná (např. Drive for desktop neběží), zobrazí se
poslední načtená kopie registru pouze pro čtení.

## Tisk

Na kartě Štítky je vlevo fronta, výběr tiskárny a tlačítka tisku, vpravo
rozměry archu. Nastavte rozměry archu, nálepek, mezery, okraje a případné
kalibrační posuny v milimetrech a uložte profil. Kliknutím na pozici vyberte
začátek; zaškrtněte již použité nebo chybějící nálepky. Evidované archy lze
vybírat opakovaně i po restartu.

Náhled PDF ani odeslání do tiskárny nálepky samo nespotřebuje. Hned po
tisku se zobrazí dotaz se všemi štítky předvybranými: povedený tisk potvrdíte
jedním tlačítkem (nebo Enter). Nepovedené štítky odškrtněte a zvolte
**Uložit jen zaškrtnuté**; zůstanou ve frontě a jejich pozice volné.
**Nic se nevytisklo** tiskovou úlohu zruší. **Rozhodnu později** ji ponechá
a výsledek potvrdíte tlačítkem **Potvrdit výsledek tisku** (také po tisku
z náhledu PDF). Větší dávka pokračuje na dalších arších.

Na Windows aplikace tiskne přímo na vybranou tiskárnu v měřítku 100 %
a zkontroluje, že formát papíru tiskárny odpovídá profilu archu. Linux
portable používá CUPS (`lp`), Flatpak otevře PDF v systémovém prohlížeči.
Nejprve ověřte zarovnání na obyčejném papíru. Při návratu částečně
použitého papíru zkontrolujte jeho ID a orientaci. Opakovaný tisk štítku
z detailu nepřiděluje nové číslo dokumentu.
