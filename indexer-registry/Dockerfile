FROM python:3.13-alpine

WORKDIR /app
COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt
COPY app.py catalog.py compatibility.py signing.py ./

EXPOSE 5050
CMD ["gunicorn", "--bind", "0.0.0.0:5050", "--workers", "2", "--access-logfile", "-", "app:app"]
