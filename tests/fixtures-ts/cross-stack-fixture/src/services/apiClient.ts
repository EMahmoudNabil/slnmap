import axios from 'axios';

const apiClient = axios.create({ baseURL: '/api/proxy' });

export default apiClient;
