<script setup lang="ts">
import { customerCopy } from '../../application/customerCopy';
</script>

<template>
  <article class="page-stack information-page" aria-labelledby="privacy-title">
    <p class="eyebrow">Wand Night Stand · DildoVander</p>
    <h1 id="privacy-title">{{ customerCopy.text('privacy.title') }}</h1>
    <p>{{ customerCopy.text('privacy.updated') }}</p>

    <section aria-labelledby="privacy-contact">
      <h2 id="privacy-contact">{{ customerCopy.text('privacy.contact-title') }}</h2>
      <p>{{ customerCopy.text('privacy.contact') }} <a href="mailto:vojtech.petrik58@gmail.com?subject=Wand%20Night%20Stand%20%E2%80%93%20privacy">vojtech.petrik58@gmail.com</a>.</p>
    </section>

    <section aria-labelledby="privacy-data">
      <h2 id="privacy-data">{{ customerCopy.text('privacy.data-title') }}</h2>
      <dl>
        <dt>{{ customerCopy.text('privacy.account-label') }}</dt>
        <dd>{{ customerCopy.text('privacy.account') }}</dd>
        <dt>{{ customerCopy.text('privacy.device-label') }}</dt>
        <dd>{{ customerCopy.text('privacy.device') }}</dd>
        <dt>{{ customerCopy.text('privacy.conversations-label') }}</dt>
        <dd>{{ customerCopy.text('privacy.conversations') }}</dd>
        <dt>{{ customerCopy.text('privacy.character-label') }}</dt>
        <dd>{{ customerCopy.text('privacy.character') }}</dd>
        <dt>{{ customerCopy.text('privacy.media-label') }}</dt>
        <dd>{{ customerCopy.text('privacy.media') }}</dd>
        <dt>{{ customerCopy.text('privacy.technical-label') }}</dt>
        <dd>{{ customerCopy.text('privacy.technical') }}</dd>
      </dl>
    </section>

    <section aria-labelledby="privacy-cloud">
      <h2 id="privacy-cloud">{{ customerCopy.text('privacy.cloud-title') }}</h2>
      <p>{{ customerCopy.text('privacy.cloud') }}</p>
      <p>{{ customerCopy.text('privacy.consent') }}</p>
      <p>{{ customerCopy.text('privacy.provider') }} <a href="https://openai.com/policies/privacy-policy/" target="_blank" rel="noopener noreferrer">{{ customerCopy.text('privacy.openai-link') }}</a>; {{ customerCopy.text('privacy.provider-limit') }}</p>
    </section>

    <section aria-labelledby="privacy-retention">
      <h2 id="privacy-retention">{{ customerCopy.text('privacy.retention-title') }}</h2>
      <p>{{ customerCopy.text('privacy.retention-intro') }}</p>
      <ul>
        <li>{{ customerCopy.text('privacy.retention-memory') }}</li>
        <li>{{ customerCopy.text('privacy.retention-keepsakes') }}</li>
        <li>{{ customerCopy.text('privacy.retention-media') }}</li>
        <li>{{ customerCopy.text('privacy.retention-logs') }}</li>
        <li>{{ customerCopy.text('privacy.retention-account') }}</li>
      </ul>
      <p>{{ customerCopy.text('privacy.retention-operator') }}</p>
    </section>

    <section aria-labelledby="privacy-sharing">
      <h2 id="privacy-sharing">{{ customerCopy.text('privacy.sharing-title') }}</h2>
      <p>{{ customerCopy.text('privacy.sharing') }}</p>
      <p>{{ customerCopy.text('privacy.preferences') }}</p>
    </section>

    <section aria-labelledby="privacy-control">
      <h2 id="privacy-control">{{ customerCopy.text('privacy.control-title') }}</h2>
      <p>{{ customerCopy.text('privacy.control') }}</p>
      <p>{{ customerCopy.text('privacy.deletion-receipt') }}</p>
      <p>{{ customerCopy.text('privacy.data-request') }}</p>
      <p><RouterLink to="/support">{{ customerCopy.text('privacy.support-link') }}</RouterLink></p>
    </section>
  </article>
</template>
