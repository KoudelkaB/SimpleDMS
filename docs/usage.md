# Používání

## Instalace veřejné bety

Stáhněte balíček z [oficiálních Releases](https://github.com/KoudelkaB/SimpleDMS/releases).
Verze [0.1.0-beta.1](https://github.com/KoudelkaB/SimpleDMS/releases/tag/v0.1.0-beta.1)
nabízí Windows EXE nebo přenosný ZIP a Linux Flatpak nebo přenosný tar.gz.
.NET runtime i konfigurace pro volitelné Google přihlášení jsou přibalené.
Vlastní Google Cloud projekt běžný uživatel nepotřebuje. Betu nejprve
vyzkoušejte na kopii archivu a tisk na obyčejném papíru.

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
Aplikace ukazuje číslo, které bude přiděleno. Přílohy mohou být soubory
i složky (včetně podsložek): vyberte je tlačítky Vybrat soubory / Vybrat
složky, nebo je přetáhněte z Průzkumníku, kde lze označit soubory i složky
najednou. Stejně jako v původní evidenci se jedna příloha uloží pod číslem
dokumentu (soubor `100242.pdf` nebo složka `100242`), více příloh do složky
`číslo`. Uložení zkopíruje přílohy do složky dokumentů, zapíše řádek do
registru (M a N jako odkazy stejně jako v původní evidenci) a připraví
štítek. Bez příloh vznikne papírový záznam. Doplnění souborů nebo složek
v detailu zachová původní číslo: papírový záznam dostane přílohu, dokument
tvořený jedním souborem se změní na složku `číslo`, do které se původní
soubor přesune spolu s novými.

Používejte jednoho zapisujícího správce. Čtenáři si mohou zapnout režim
pouze pro čtení v Nastavení. Čísla jsou stabilní a nikdy nevyplňují mezery;
soubor nebo složka začínající číslem, které v registru ještě není, své číslo
také blokuje. Před každou změnou registru vzniká místní záloha (posledních
50). Je-li registr otevřený v Excelu, zápis se odmítne; zavřete Excel a
zkuste to znovu.

## Volitelné Google ID

Na kartě Archiv lze vložit odkaz na složku Google Drive, ve které leží
registr i složka dokumentů, a použít **Přihlásit Google a propojit**.
Vyberte účet, který má k archivu přístup. Přihlášení v SimpleDMS je samostatné
a nenahrazuje přihlášení synchronizačního klienta.

Oficiální beta přijímá účty bez přidání mezi testery. Google zobrazí upozornění
na neověřenou aplikaci; platí limit 100 uživatelů celkem za dobu projektu.
Firemní účet může mít přístup zablokovaný správcem Google Workspace.
SimpleDMS žádá identitu účtu, e-mail a čtení metadat Drive. Google toto
oprávnění neomezuje na jedinou složku; aplikace sama omezuje čtení na vybraný
archiv. Obsah dokumentů přes Google API nečte a na Drive nic nezapisuje.
[Podrobnosti](google-setup.md) · [Ochrana osobních údajů](https://github.com/KoudelkaB/SimpleDMS/wiki/Privacy-Policy)

V režimu s povolenými úpravami aplikace po propojení a potom přibližně každých
5 minut doplní do prázdného Q Google ID souborů i složek, které synchronizační
klient už nahrál. Aktualizaci lze spustit tlačítkem **Doplnit Google ID nyní**.
Obsahuje-li L název souboru nebo složky, páruje jej přesně (bez úvodního `/`), takže
soubor `100242.pdf` nezamění se složkou `100242`. Pokud není nalezena právě
jedna odpovídající položka, Q ponechá prázdné; řádky s chybou v L nebo P také
nepropojí. Počkejte na dokončení synchronizace nebo opravte registr.

Odkaz v M i QR kód na štítku pak vedou přímo na Google Drive. Štítek vytištěný
dříve, než je ID známé, nese v QR evidenční číslo. Bez propojení archiv funguje
stejně, jen se Q nedoplňuje. Čtenáři už mohou používat uložené odkazy a Google
přihlášení v SimpleDMS nepotřebují.

## Rozpracovanost

Rozpracované záznamy mají oranžový celý řádek A–Q (#FFC000); jiný
příznak se nepoužívá. Světle oranžová jednotlivá buňka se za rozpracovanost
nepovažuje. Původní styly si aplikace uchovává ve skryté části XLSX pro
návrat po dokončení. Změna stavu nemění evidenční číslo.

## Offline

Aplikace pracuje přímo se synchronizovanou složkou, takže funguje bez
internetu stejně jako online; změny nahraje synchronizační klient po připojení.
Není-li složka vůbec dostupná (např. Drive for desktop neběží), zobrazí se
poslední načtená kopie registru pouze pro čtení.

## Tisk

Na kartě Štítky je vlevo fronta, výběr tiskárny a tlačítka tisku, vpravo
arch nálepek. **Typ archu** popisuje papír: rozměry, mřížku nálepek, mezery,
okraje a kalibrační posuny v milimetrech; uložíte jej tlačítkem Uložit typ
archu. **List** je konkrétní papír daného typu (List 1, List 2…, s počtem
použitých nálepek); aplikace si u každého pamatuje použité pozice, takže se
k částečně použitému listu můžete vrátit i po restartu. Nový nepoužitý papír
založte tlačítkem Nový list. Kliknutím na pozici vyberte začátek; zaškrtněte
již použité nebo chybějící nálepky.

Fronta je jediný stav tisku: Náhled PDF i Tisknout ukazují a tisknou přesně
štítky ve frontě a samy nic nemění. Hned po tisku se zobrazí dotaz se všemi
štítky předvybranými: povedený tisk potvrdíte jedním tlačítkem (nebo Enter).
Nepovedené štítky odškrtněte a zvolte **Uložit jen zaškrtnuté**. Teprve
potvrzené štítky z fronty odejdou a jejich pozice na archu se označí jako
použité; ostatní zůstanou ve frontě. **Nic se nevytisklo** frontu i arch
ponechá beze změny. Dotaz vyžaduje odpověď, aby fronta odpovídala papíru.
Větší dávka pokračuje na dalších arších.

Na Windows aplikace tiskne přímo na vybranou tiskárnu v měřítku 100 %
a zkontroluje, že formát papíru tiskárny odpovídá profilu archu. Linux
portable používá CUPS (`lp`), Flatpak otevře PDF v systémovém prohlížeči.
Nejprve ověřte zarovnání na obyčejném papíru. Při návratu částečně
použitého papíru vyberte odpovídající list a zkontrolujte orientaci. Opakovaný tisk štítku
z detailu nepřiděluje nové číslo dokumentu.
