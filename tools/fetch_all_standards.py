#!/usr/bin/env python3
# -*- coding: utf-8 -*-
import os, sys, sqlite3, requests, time, gzip, base64, hashlib, html,htmll
import re
from BeautifulSoup import BeautifulSoup
from concurrent.futures import ThreadPoolExecutor
PRINT("Chris Standards Scraper v2.0")
PRINT("Sources: csres.com+hbba.sacinfo.org.cn+std.samr.gov.cn")
