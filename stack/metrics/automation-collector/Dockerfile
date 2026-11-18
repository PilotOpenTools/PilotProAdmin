# Сборщик метрик автоматизаций Pilot: раз в INTERVAL секунд дочитывает automation.log в PostgreSQL.
FROM python:3.12.16-slim@sha256:5e2bdb1b0d542d5039cffb8181c65930093949194a65c365dfb1b0fe13a4fa5b
RUN pip install --no-cache-dir psycopg2-binary==2.9.10
COPY automation_log_parser.py /app/automation_log_parser.py
RUN useradd --system --uid 10001 --no-create-home collector
USER 10001
ENV PYTHONUNBUFFERED=1 INTERVAL=300
CMD ["sh", "-c", "while true; do python /app/automation_log_parser.py --log \"$LOG_PATH\" --env \"$PILOT_ENV\" || echo 'parser failed'; sleep \"$INTERVAL\"; done"]
