-- SystemGuard Shop DB (Supabase / Postgres). Выполнить один раз в Supabase → SQL Editor.
-- RLS включён. Desktop-приложение читает СВОЙ ключ по HWID через anon-ключ
-- (кнопка «Я оплатил, проверить»), пишет только shop-bot через service key.

create table if not exists public.customers (
  telegram_id bigint primary key,
  username text,
  hwid text not null default '',
  created_at timestamptz not null default now()
);

create table if not exists public.licenses (
  id bigserial primary key,
  telegram_id bigint not null references public.customers(telegram_id) on delete cascade,
  username text,
  hwid text not null default '',
  tier text not null default 'Pro',
  plan text not null default 'Monthly',
  expires_at timestamptz not null,
  key_prefix text not null default '',
  payment_ref text not null default '',
  revoked boolean not null default false,
  created_at timestamptz not null default now()
);
create index if not exists licenses_hwid_idx on public.licenses (hwid);
create index if not exists licenses_tg_idx on public.licenses (telegram_id);

create table if not exists public.payments (
  id bigserial primary key,
  telegram_id bigint not null,
  provider text not null default 'stars',
  payload text not null default '',
  amount text not null default '',
  status text not null default 'paid',
  created_at timestamptz not null default now()
);

alter table public.customers enable row level security;
alter table public.licenses enable row level security;
alter table public.payments enable row level security;

-- Anon-ключ: только чтение СВОЕГО активного ключа по точному HWID
-- (HWID 32 hex — перебор нерентабелен; для параноиков добавь rate-limit на PostgREST).
drop policy if exists "read own license by hwid" on public.licenses;
create policy "read own license by hwid" on public.licenses
  for select to anon
  using (revoked = false and expires_at > now());

-- Service key (shop-bot): полный доступ, RLS обходится автоматически.
-- Никому не показывай service key: только сервер shop-bot.

-- Управление пользователями (SQL для владельца):
-- Продлить:  update public.licenses set expires_at = expires_at + interval '30 days' where telegram_id = <id>;
-- Забанить:  update public.licenses set revoked = true where telegram_id = <id>;
-- Мульти-ПК: разреши N строк licenses с разными hwid на один telegram_id (Enterprise);
--   проверка лимита — в shop-bot перед выдачей (select count(*) ...).

-- ── activations: какой ПК (hwid + имя) каким ключом пользуется ─────────────
-- Приложение само пишет сюда строку при активации ключа и раз в сутки при
-- запущенной Pro/Enterprise (fire-and-forget, тихо). Полного ключа тут нет —
-- только key_prefix (первые 28 символов ключа, как в licenses), сопоставляешь с
-- licenses.key_prefix и видишь, кто где сидит. Старые строки продаж могут иметь
-- префикс из 12 символов — они матчатся через LIKE 'prefix%'.
-- Anon-ключ умеет ТОЛЬКО вставлять строки (читать/менять/тереть — только
-- владелец через Table Editor / service key), так что бот и приложение
-- ничего чужого не увидят, а спам лечится фильтром по своим key_prefix.
create table if not exists public.activations (
  id bigserial primary key,
  hwid text not null default '',
  machine text not null default '',
  key_prefix text not null default '',
  tier text not null default '',
  plan text not null default '',
  app_version text not null default '',
  created_at timestamptz not null default now()
);
create index if not exists activations_hwid_idx on public.activations (hwid);
create index if not exists activations_prefix_idx on public.activations (key_prefix);
alter table public.activations enable row level security;
drop policy if exists "anon insert activations" on public.activations;
create policy "anon insert activations" on public.activations
  for insert to anon
  with check (hwid <> '' and key_prefix <> '');

-- Кто где сидит (последняя активность по каждой паре ключ-ПК):
-- select distinct on (key_prefix, hwid)
--   key_prefix, hwid, machine, tier, plan, app_version, created_at as last_seen
-- from public.activations
-- order by key_prefix, hwid, created_at desc;
-- Все активации одного ключа:
-- select hwid, machine, tier, plan, app_version, created_at
-- from public.activations where key_prefix = 'SG-PRO-XXXX' order by created_at desc;
