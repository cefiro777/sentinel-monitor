# Как помочь проекту

Спасибо, что хотите помочь! Полезно всё: сообщения об ошибках, идеи новых проверок, исправления и документация.

## Сообщить об ошибке или предложить идею

- Ошибка — [новая задача](https://github.com/cefiro777/sentinel-monitor/issues/new/choose) по шаблону «Ошибка»: версия, как воспроизвести, логи.
- Идея — шаблон «Предложение»: какую задачу из практики это решает.
- Вопрос по настройке — в [обсуждениях](https://github.com/cefiro777/sentinel-monitor/discussions).
- Уязвимость — только приватно, см. [SECURITY.md](SECURITY.md).

**Не публикуйте реальные данные**: пароли, токены, адреса серверов, имена клиентов — замените на условные (`monitor.example.com`, `SRV-01`, `Клиент А`).

## Внести изменения

1. Сделайте fork и ветку от `main`.
2. Подготовьте окружение (см. «Разработка» в [README](README.md)): .NET SDK 10, .NET Framework 4.8 Developer Pack, Node.js 20+, Docker.
3. Перед pull request:
   ```bash
   dotnet build
   dotnet test tests/Sentinel.Contracts.Tests
   dotnet test tests/Sentinel.Server.Tests
   cd src/Sentinel.Web && npm run build
   ```
4. Изменили схему БД — добавьте миграцию:
   `dotnet ef migrations add <Name> --project src/Sentinel.Server.Data --startup-project src/Sentinel.Server`.
5. Изменили протокол агент ↔ сервер — сохраните совместимость со старыми агентами (они обновляются не сразу).

## Соглашения

- Интерфейс, сообщения и комментарии в коде — на русском; имена в коде — на английском.
- Агент — .NET Framework 4.8 и Windows Server 2008 R2: без API новее, чем там есть.
- Комментарии объясняют «почему», а не «что».
- Новый модуль проверки: настройки в `Sentinel.Contracts/Modules`, реализация в `Sentinel.Agent/Modules`, форма в `Sentinel.Web/src/components/ModuleForms.tsx`.

Отправляя pull request, вы соглашаетесь, что ваш вклад распространяется под лицензией проекта ([AGPL-3.0](LICENSE)).
