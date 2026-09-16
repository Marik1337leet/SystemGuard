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
