# Cloudflare Visits Tray

Mały program na Windowsa, który pokazuje liczbę odwiedzin strony z ostatnich 24 godzin bezpośrednio na pasku zadań. Dane pochodzą z Cloudflare Web Analytics.

![Licznik na pasku zadań](docs/podglad.png)

## Funkcje

- adres strony i liczba odwiedzin stale widoczne na pasku zadań,
- ikona z liczbą w zasobniku systemowym,
- okno z wyświetleniami i najczęściej oglądanymi podstronami,
- odświeżanie co 5 minut,
- jasny i ciemny motyw,
- automatyczne chowanie przy aplikacjach pełnoekranowych.

Program pokazuje wizyty, a nie unikalne osoby.

## Wymagania

- Windows 10 lub 11,
- .NET Framework 4.8, obecny w systemie,
- strona z włączonym Cloudflare Web Analytics,
- klucz API Cloudflare z uprawnieniem `Account Analytics: Read`.

## Budowanie

W PowerShellu, w głównym folderze projektu:

```powershell
powershell -ExecutionPolicy Bypass -File src\build.ps1
```

Kompilator jest częścią Windowsa, więc nie trzeba niczego instalować. Program pojawi się jako `LicznikOdwiedzin.exe`.

## Konfiguracja

Przy pierwszym uruchomieniu program zapyta o adres strony, identyfikator konta Cloudflare i klucz API. Użyj klucza tylko do odczytu, a nie klucza globalnego.

## Testy

```powershell
.\LicznikOdwiedzin.exe --selftest wyniki.txt
```

## Prywatność

Klucz API jest szyfrowany mechanizmem Windows DPAPI i zapisywany lokalnie w folderze `%APPDATA%\LicznikOdwiedzin`. Program wysyła go wyłącznie do API Cloudflare.

## Technologie

C#, Windows Forms, .NET Framework 4.8, Cloudflare GraphQL API.
