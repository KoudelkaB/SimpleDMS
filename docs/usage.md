# Používání

## Otevření a přidání

Na kartě Archiv vložte odkaz na kořenovou složku Google Drive a přihlaste
se vlastním účtem. SimpleDMS najde dvojici `Jméno archivu.xlsx` a
`Jméno archivu/`. Jedinou dvojici otevře automaticky; více dvojic nabídne
k výběru. Chybějící části vytvoří správce se zápisem. Jméno registru
se zobrazí v hlavičce. Starší složka s podtržítky místo mezer je podporována.

Zadejte kategorii (2 číslice), název, případně další metadata a přílohy.
Soubory i celé složky lze vybrat nebo přetáhnout do seznamu. Aplikace přidělí
šestimístný kód, nahraje přílohy do složky tohoto dokumentu, zapíše metadata
a Q (skutečné Drive ID) a připraví štítek. Bez příloh vznikne papírový záznam.
Doplnění příloh v detailu zachová původní číslo.

Používejte jednoho zapisujícího správce. Čtenáři si mohou zapnout čtenářský
režim v Nastavení. Čísla jsou stabilní; lokálně rezervovaná čísla se při
obnově přerušení znovu použijí pro stejnou operaci. U každé změny XLSX vzniká
místní záloha. Při zjištění souběžné změny se zápis zastaví. Kontrola verze
není globální distribuovaný zámek: stejný XLSX neupravujte současně v Excelu
nebo v druhé zapisující instanci.

## Rozpracovanost

Rozpracované legacy záznamy mají oranžový celý řádek A–Q (#FFC000).
Světle oranžová jednotlivá buňka se za rozpracovanost nepovažuje.
Aplikace zapisuje explicitní stav do R a aktualizuje oranžové zvýraznění.
Původní styly si uchovává v části XLSX pro návrat po dokončení.
Změna stavu nemění evidenční číslo.

## Offline

Evidence z posledního otevření je v lokální cache, takže vyhledávání funguje
bez internetu. Pro přílohy zvolte jednu možnost na kartě Archiv:

- **Připojit synchronizovanou složku**: vyberte root s XLSX a složkou
  dokumentů. Google Drive desktop / Insync / rclone obstará přenos dat.
  Nastavte dostupnost celé složky offline v tomto klientovi; samotné Q
  soubory nestahuje. Online se vytvoří mapa Drive ID na místní cesty.
- **Vytvořit offline kopii**: SimpleDMS založí samostatnou složku pod
  vybraným umístěním a stáhne registr a dokumenty. Za běhu kontroluje archiv
  po pěti minutách, aktualizuje změněné soubory a ověřuje kontrolní součty.

Vlastní kopie je jednosměrná. Místní změny se nenahrávají a nahrazované
místně upravené soubory se zálohují. Smazané cloudové soubory se z lokálního
disku automaticky nemažou, ale přestanou být mapované v nové evidenci.
Google Docs se exportují do PDF, Sheets do XLSX; zástupce vyžaduje přípravu
cíle mimo aplikaci. Po přerušení spusťte aktualizaci znovu. Aplikace
nesynchronizuje, když neběží.

## Tisk

Na kartě Štítky nastavte rozměry archu, nálepek, mezery, okraje a případné
kalibrační posuny v milimetrech. Uložte profil. Kliknutím na pozici vyberte
začátek; zaškrtněte již použité nebo chybějící nálepky. Evidované archy lze
vybírat opakovaně i po restartu.

Náhled/export PDF ani odeslání do tiskárny nálepky nespotřebuje. Po tisku
zvolte **Potvrdit výsledek tisku** a označte skutečně vytištěné pozice.
Při chybě lze potvrdit jen část a zbytek zůstane ve frontě. Větší dávka
pokračuje na dalších arších; v dialogu je číslo stránky i pozice.

Tiskněte ve velikosti 100 %, bez přizpůsobení stránce. Nejprve ověřte
zarovnání na obyčejném papíru. Windows používá tisk PDF prohlížeče,
Linux portable CUPS (`lp`), Flatpak otevře PDF v systémovém prohlížeči.
Při návratu částečně použitého papíru zkontrolujte jeho ID a orientaci.
Opakovaný tisk štítku z detailu nepřiděluje nové číslo dokumentu.
