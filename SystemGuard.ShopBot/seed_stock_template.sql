-- Складские ключи БЕЗ пользователей: сгенерировал KeyGenerator'ом (переносимые,
-- HWID пустой), вставляешь в Supabase → SQL Editor, потом раздаёшь сам.
-- Когда ключ активируют на ПК, приложение само запишет строку в activations
-- (hwid + имя ПК + key_prefix) — увидишь, кто где сидит. Тогда же в строке
-- ниже проставишь покупателю telegram_id/username/hwid.
--
-- ВАЖНО: полные ключи живут только у тебя, в репозиторий их НЕ коммитить.
-- Замени <KEY_1..5> и <PREFIX_1..5> (первые 28 символов ключа) на настоящие,
-- даты expires_at — на даты из генератора (срок внутри ключа).
--
-- Выполнять ПОСЛЕ supabase_schema.sql (нужны таблицы customers/licenses).

-- Складской "покупатель" (FK licenses→customers требует строку):
insert into public.customers (telegram_id, username, hwid)
values (0, 'stock', '')
on conflict (telegram_id) do nothing;

-- Сами ключи (пример: 3 Monthly + 1 Yearly + 1 Lifetime — правь под себя):
insert into public.licenses (telegram_id, username, hwid, tier, plan, expires_at, key_prefix, payment_ref) values
(0, 'stock', '', 'Pro', 'Monthly', '<EXPIRES_YYYY-MM-DD>', '<PREFIX_1>', 'manual-stock-1'),
(0, 'stock', '', 'Pro', 'Monthly', '<EXPIRES_YYYY-MM-DD>', '<PREFIX_2>', 'manual-stock-2'),
(0, 'stock', '', 'Pro', 'Monthly', '<EXPIRES_YYYY-MM-DD>', '<PREFIX_3>', 'manual-stock-3'),
(0, 'stock', '', 'Pro', 'Yearly',  '<EXPIRES_YYYY-MM-DD>', '<PREFIX_4>', 'manual-stock-4'),
(0, 'stock', '', 'Pro', 'Lifetime','<EXPIRES_YYYY-MM-DD>', '<PREFIX_5>', 'manual-stock-5');

-- Полные ключи <KEY_1..5> выдаёшь покупателям вручную (бот/личка).
-- Когда ключ забрали — привяжи строку к человеку:
-- update public.licenses
--   set telegram_id = <TG_ID>, username = '<name>', hwid = '<HWID_ПК>',
--       payment_ref = 'manual-stock-N sold <дата>'
-- where key_prefix = '<PREFIX_N>';

-- Кто где сидит (последняя активность по каждой паре ключ-ПК):
-- select distinct on (key_prefix, hwid)
--   key_prefix, hwid, machine, tier, plan, app_version, created_at as last_seen
-- from public.activations
-- order by key_prefix, hwid, created_at desc;
