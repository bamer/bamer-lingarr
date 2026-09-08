-- =====================================================================
-- Diagnostic "traduction manquante non détectée" (ex: Airplane II —
-- sources en/fr présentes, cible th manquante, jamais re-mise en file).
--
-- Usage (depuis une machine joignant le serveur MariaDB) :
--   docker run --rm -i mariadb:latest mariadb -h 192.168.1.117 \
--     -u lingarr -p<LINGARR_DB_PASSWORD> Lingarr < diagnose_missing_translations.sql
--
-- Rappel : la table translation_requests stocke `status` en ENTIER :
--   0=Pending 1=InProgress 2=Completed 3=Failed 4=Cancelled
--   5=Interrupted 6=Partial
-- =====================================================================

-- 1. Toutes les requêtes liées à un média précis (ajuster le LIKE).
--    Une requête en 0 (Pending) ou 1 (InProgress) BLOQUE sa langue cible :
--    l'automatisation rapportera le média "already up to date" tant
--    qu'elle existe, même si le job Hangfire ne tournera jamais.
SELECT id, media_id, title, source_language, target_language, status,
       created_at, completed_at, job_id, error_message,
       subtitle_to_translate, translated_subtitle
FROM translation_requests
WHERE subtitle_to_translate LIKE '%Airplane II - The Sequel (1982)%'
ORDER BY created_at DESC;

-- 2. Vue d'ensemble : combien de requêtes actives bloquent des cibles.
SELECT status, COUNT(*) AS requests
FROM translation_requests
WHERE status IN (0, 1)
GROUP BY status;

-- 3. Les plus vieilles requêtes actives = les cibles bloquées depuis le
--    plus longtemps. Croiser job_id avec _hangfire_job (voir requête 5) :
--    si le job n'existe plus (crash, purge) ou est en échec, la requête
--    ne se terminera jamais d'elle-même.
SELECT id, title, target_language, status, created_at, job_id
FROM translation_requests
WHERE status IN (0, 1)
ORDER BY created_at ASC
LIMIT 25;

-- 4. Cibles satisfaites sur le papier mais sans fichier de sortie :
--    statut Completed (2) avec translated_subtitle vide ou NULL.
--    Depuis la v2.24.x elles sont re-mises en file ; si vous en voyez
--    beaucoup ici avec une vieille version, voilà les "traductions
--    complétées" fantômes.
SELECT id, title, target_language, status, completed_at, translated_subtitle
FROM translation_requests
WHERE status = 2
  AND (translated_subtitle IS NULL OR translated_subtitle = '')
ORDER BY completed_at DESC
LIMIT 25;

-- 5. État Hangfire des jobs des requêtes actives (MySQL/MariaDB).
--    Tables : _hangfireJob / _hangfireJobQueue (prefix _hangfire configuré par Lingarr).
--    state_name NULL ou 'Deleted'/'Failed'/'Expired' avec une requête
--    active = job orphelin : la requête ne se débloquera qu'au prochain
--    passage d'automatisation (ReconcileStaleRequests, seuil
--    automation_stale_request_hours, défaut 12 h).
SELECT tr.id AS request_id, tr.title, tr.target_language, tr.status,
       tr.created_at, tr.job_id, hj.StateName AS state_name
FROM translation_requests tr
LEFT JOIN _hangfireJob hj ON CAST(hj.Id AS CHAR) = tr.job_id
WHERE tr.status IN (0, 1)
ORDER BY tr.created_at ASC
LIMIT 50;

-- 6. Profondeur de la file de traductions (backlog réel).
SELECT COUNT(*) AS queued_translations
FROM _hangfireJobQueue
WHERE Queue = 'translation';

-- 7. Répartition des états Hangfire des jobs.
SELECT hj.StateName AS state_name, COUNT(*) AS jobs
FROM _hangfireJob hj
GROUP BY hj.StateName;

-- 7. Médias exclus du scan (IncludeInTranslation = 0) — ils ne sont
--    jamais scannés et expliquent des cibles "jamais détectées".
SELECT
  (SELECT COUNT(*) FROM movies   WHERE include_in_translation = 0) AS excluded_movies,
  (SELECT COUNT(*) FROM shows    WHERE include_in_translation = 0) AS excluded_shows,
  (SELECT COUNT(*) FROM seasons  WHERE include_in_translation = 0) AS excluded_seasons,
  (SELECT COUNT(*) FROM episodes WHERE include_in_translation = 0) AS excluded_episodes;
